using McpDatabaseQueryApp.Core.QueryParsing;
using PgSqlParser;

namespace McpDatabaseQueryApp.Providers.Postgres.QueryParsing;

/// <summary>
/// Walks one libpg_query top-level statement node and produces the
/// matching list of <see cref="ParsedQueryAction"/>s along with column
/// references. The walker is purely state-based (no visitor pattern is
/// available on the protobuf-generated nodes).
/// </summary>
internal sealed class PgActionExtractor
{
    private readonly List<ParsedQueryAction> _actions = [];
    private readonly List<ColumnReference> _columns = [];
    private readonly HashSet<string> _cteNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Runs the walker against <paramref name="stmt"/> and returns the
    /// extracted actions / columns.
    /// </summary>
    public (IReadOnlyList<ParsedQueryAction> Actions, IReadOnlyList<ColumnReference> Columns) Extract(Node stmt)
    {
        WalkStatement(stmt);

        if (_columns.Count > 0 && _actions.Count > 0)
        {
            var index = FindPrimaryActionIndex();
            var existing = _actions[index];
            var merged = existing.Columns.Count == 0
                ? (IReadOnlyList<ColumnReference>)_columns
                : [.. existing.Columns, .. _columns];
            _actions[index] = existing with { Columns = merged };
        }

        return (_actions, _columns);
    }

    private int FindPrimaryActionIndex()
    {
        for (var i = 0; i < _actions.Count; i++)
        {
            var k = _actions[i].Action;
            if (k is ActionKind.Insert or ActionKind.Update or ActionKind.Delete)
            {
                return i;
            }
        }
        for (var i = 0; i < _actions.Count; i++)
        {
            if (_actions[i].Action == ActionKind.Read)
            {
                return i;
            }
        }
        return 0;
    }

    private void WalkStatement(Node stmt)
    {
        switch (stmt.NodeCase)
        {
            case Node.NodeOneofCase.SelectStmt:
                WalkSelect(stmt.SelectStmt);
                break;
            case Node.NodeOneofCase.InsertStmt:
                WalkInsert(stmt.InsertStmt);
                break;
            case Node.NodeOneofCase.UpdateStmt:
                WalkUpdate(stmt.UpdateStmt);
                break;
            case Node.NodeOneofCase.DeleteStmt:
                WalkDelete(stmt.DeleteStmt);
                break;
            case Node.NodeOneofCase.MergeStmt:
                WalkMerge(stmt.MergeStmt);
                break;
            case Node.NodeOneofCase.TruncateStmt:
                WalkTruncate(stmt.TruncateStmt);
                break;
            case Node.NodeOneofCase.CreateStmt:
                _actions.Add(ParsedQueryAction.Of(
                    ActionKind.Create,
                    stmt.CreateStmt.Relation.ToObjectReference(ObjectKind.Table)));
                break;
            case Node.NodeOneofCase.AlterTableStmt:
                _actions.Add(ParsedQueryAction.Of(
                    ActionKind.Alter,
                    stmt.AlterTableStmt.Relation.ToObjectReference(ObjectKind.Table)));
                break;
            case Node.NodeOneofCase.DropStmt:
                WalkDrop(stmt.DropStmt);
                break;
            case Node.NodeOneofCase.ViewStmt:
                {
                    var v = stmt.ViewStmt;
                    _actions.Add(ParsedQueryAction.Of(
                        ActionKind.Create,
                        v.View.ToObjectReference(ObjectKind.View)));
                    if (v.Query is not null && v.Query.NodeCase == Node.NodeOneofCase.SelectStmt)
                    {
                        WalkSelect(v.Query.SelectStmt);
                    }
                    break;
                }
            case Node.NodeOneofCase.IndexStmt:
                {
                    var idx = stmt.IndexStmt;
                    var indexRef = new ObjectReference(
                        ObjectKind.Index, null, null, null, idx.Idxname ?? string.Empty);
                    _actions.Add(ParsedQueryAction.Of(ActionKind.Create, indexRef));
                    if (idx.Relation is not null)
                    {
                        _actions.Add(ParsedQueryAction.Of(
                            ActionKind.Alter,
                            idx.Relation.ToObjectReference(ObjectKind.Table)));
                    }
                    break;
                }
            case Node.NodeOneofCase.CreateSchemaStmt:
                {
                    var cs = stmt.CreateSchemaStmt;
                    var schemaRef = new ObjectReference(
                        ObjectKind.Schema, null, null, null, cs.Schemaname ?? string.Empty);
                    _actions.Add(ParsedQueryAction.Of(ActionKind.Create, schemaRef));
                    break;
                }
            case Node.NodeOneofCase.CreateFunctionStmt:
                {
                    var fn = stmt.CreateFunctionStmt;
                    var fnRef = PgNameExtensions.ListToObjectReference(fn.Funcname, ObjectKind.Function);
                    _actions.Add(ParsedQueryAction.Of(ActionKind.Create, fnRef));
                    break;
                }
            case Node.NodeOneofCase.AlterFunctionStmt:
                {
                    var fn = stmt.AlterFunctionStmt;
                    if (fn.Func is not null)
                    {
                        var fnRef = PgNameExtensions.ListToObjectReference(fn.Func.Objname, ObjectKind.Function);
                        _actions.Add(ParsedQueryAction.Of(ActionKind.Alter, fnRef));
                    }
                    break;
                }
            case Node.NodeOneofCase.GrantStmt:
                WalkGrant(stmt.GrantStmt);
                break;
            case Node.NodeOneofCase.CallStmt:
                {
                    var call = stmt.CallStmt;
                    if (call.Funccall is not null)
                    {
                        var funcRef = PgNameExtensions.ListToObjectReference(call.Funccall.Funcname, ObjectKind.Procedure);
                        _actions.Add(ParsedQueryAction.Of(ActionKind.Execute, funcRef));
                    }
                    break;
                }
            case Node.NodeOneofCase.ExecuteStmt:
                {
                    var ex = stmt.ExecuteStmt;
                    var p = new ObjectReference(ObjectKind.Procedure, null, null, null, ex.Name ?? string.Empty);
                    _actions.Add(ParsedQueryAction.Of(ActionKind.Execute, p));
                    break;
                }
            default:
                break;
        }
    }

    // ---- DML helpers ---------------------------------------------------

    private void WalkSelect(SelectStmt select)
    {
        WalkWith(select.WithClause);
        foreach (var f in select.FromClause)
        {
            WalkFromItem(f);
        }
        if (select.WhereClause is not null)
        {
            WalkExpression(select.WhereClause, ColumnUsage.Filtered);
        }
        foreach (var t in select.TargetList)
        {
            if (t.NodeCase == Node.NodeOneofCase.ResTarget && t.ResTarget.Val is not null)
            {
                WalkExpression(t.ResTarget.Val, ColumnUsage.Projected);
            }
        }
        foreach (var s in select.SortClause)
        {
            if (s.NodeCase == Node.NodeOneofCase.SortBy && s.SortBy.Node is not null)
            {
                WalkExpression(s.SortBy.Node, ColumnUsage.OrderedBy);
            }
        }
        foreach (var g in select.GroupClause)
        {
            WalkExpression(g, ColumnUsage.GroupedBy);
        }
        if (select.HavingClause is not null)
        {
            WalkExpression(select.HavingClause, ColumnUsage.Filtered);
        }
        // Set-operation arms.
        if (select.Larg is not null) { WalkSelect(select.Larg); }
        if (select.Rarg is not null) { WalkSelect(select.Rarg); }
    }

    private void WalkInsert(InsertStmt insert)
    {
        WalkWith(insert.WithClause);
        var target = insert.Relation.ToObjectReference(ObjectKind.Table);
        var inserted = new List<ColumnReference>();
        foreach (var c in insert.Cols)
        {
            if (c.NodeCase == Node.NodeOneofCase.ResTarget)
            {
                var name = c.ResTarget.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    inserted.Add(new ColumnReference(target.Schema, target.Name, name, ColumnUsage.Inserted));
                }
            }
        }
        _actions.Add(new ParsedQueryAction(ActionKind.Insert, target, inserted));

        if (insert.SelectStmt is not null && insert.SelectStmt.NodeCase == Node.NodeOneofCase.SelectStmt)
        {
            WalkSelect(insert.SelectStmt.SelectStmt);
        }
    }

    private void WalkUpdate(UpdateStmt update)
    {
        WalkWith(update.WithClause);
        var target = update.Relation.ToObjectReference(ObjectKind.Table);
        var modified = new List<ColumnReference>();
        foreach (var t in update.TargetList)
        {
            if (t.NodeCase == Node.NodeOneofCase.ResTarget)
            {
                var name = t.ResTarget.Name;
                if (!string.IsNullOrEmpty(name))
                {
                    modified.Add(new ColumnReference(target.Schema, target.Name, name, ColumnUsage.Modified));
                }
                if (t.ResTarget.Val is not null)
                {
                    WalkExpression(t.ResTarget.Val, ColumnUsage.Projected);
                }
            }
        }
        _actions.Add(new ParsedQueryAction(ActionKind.Update, target, modified));

        foreach (var f in update.FromClause)
        {
            WalkFromItem(f);
        }
        if (update.WhereClause is not null)
        {
            WalkExpression(update.WhereClause, ColumnUsage.Filtered);
        }
    }

    private void WalkDelete(DeleteStmt delete)
    {
        WalkWith(delete.WithClause);
        var target = delete.Relation.ToObjectReference(ObjectKind.Table);
        _actions.Add(ParsedQueryAction.Of(ActionKind.Delete, target));

        foreach (var u in delete.UsingClause)
        {
            WalkFromItem(u);
        }
        if (delete.WhereClause is not null)
        {
            WalkExpression(delete.WhereClause, ColumnUsage.Filtered);
        }
    }

    private void WalkMerge(MergeStmt merge)
    {
        if (merge.Relation is not null)
        {
            _actions.Add(ParsedQueryAction.Of(
                ActionKind.Update,
                merge.Relation.ToObjectReference(ObjectKind.Table)));
        }
        if (merge.SourceRelation is not null)
        {
            WalkFromItem(merge.SourceRelation);
        }
    }

    private void WalkTruncate(TruncateStmt truncate)
    {
        foreach (var rel in truncate.Relations)
        {
            if (rel.NodeCase == Node.NodeOneofCase.RangeVar)
            {
                _actions.Add(ParsedQueryAction.Of(
                    ActionKind.Truncate,
                    rel.RangeVar.ToObjectReference(ObjectKind.Table)));
            }
        }
    }

    private void WalkDrop(DropStmt drop)
    {
        var kind = drop.RemoveType switch
        {
            ObjectType.ObjectTable => ObjectKind.Table,
            ObjectType.ObjectView or ObjectType.ObjectMatview => ObjectKind.View,
            ObjectType.ObjectIndex => ObjectKind.Index,
            ObjectType.ObjectSchema => ObjectKind.Schema,
            ObjectType.ObjectFunction or ObjectType.ObjectRoutine => ObjectKind.Function,
            ObjectType.ObjectProcedure => ObjectKind.Procedure,
            ObjectType.ObjectSequence => ObjectKind.Sequence,
            ObjectType.ObjectType => ObjectKind.Type,
            _ => ObjectKind.Other,
        };

        foreach (var obj in drop.Objects)
        {
            ObjectReference target;
            if (obj.NodeCase == Node.NodeOneofCase.List)
            {
                target = PgNameExtensions.ListToObjectReference(obj.List.Items, kind);
            }
            else if (obj.NodeCase == Node.NodeOneofCase.String)
            {
                target = new ObjectReference(kind, null, null, null, obj.String.Sval ?? string.Empty);
            }
            else if (obj.NodeCase == Node.NodeOneofCase.ObjectWithArgs)
            {
                target = PgNameExtensions.ListToObjectReference(obj.ObjectWithArgs.Objname, kind);
            }
            else
            {
                continue;
            }
            _actions.Add(ParsedQueryAction.Of(ActionKind.Drop, target));
        }
    }

    private void WalkGrant(GrantStmt grant)
    {
        var action = grant.IsGrant ? ActionKind.Grant : ActionKind.Revoke;
        var kind = grant.Objtype switch
        {
            ObjectType.ObjectTable => ObjectKind.Table,
            ObjectType.ObjectFunction => ObjectKind.Function,
            ObjectType.ObjectProcedure => ObjectKind.Procedure,
            ObjectType.ObjectSchema => ObjectKind.Schema,
            ObjectType.ObjectSequence => ObjectKind.Sequence,
            _ => ObjectKind.Other,
        };
        foreach (var obj in grant.Objects)
        {
            ObjectReference target;
            if (obj.NodeCase == Node.NodeOneofCase.RangeVar)
            {
                target = obj.RangeVar.ToObjectReference(kind);
            }
            else if (obj.NodeCase == Node.NodeOneofCase.List)
            {
                target = PgNameExtensions.ListToObjectReference(obj.List.Items, kind);
            }
            else if (obj.NodeCase == Node.NodeOneofCase.ObjectWithArgs)
            {
                target = PgNameExtensions.ListToObjectReference(obj.ObjectWithArgs.Objname, kind);
            }
            else if (obj.NodeCase == Node.NodeOneofCase.String)
            {
                target = new ObjectReference(kind, null, null, null, obj.String.Sval ?? string.Empty);
            }
            else
            {
                continue;
            }
            _actions.Add(ParsedQueryAction.Of(action, target));
        }
    }

    // ---- Sub-walkers ---------------------------------------------------

    private void WalkWith(WithClause? with)
    {
        if (with is null)
        {
            return;
        }
        foreach (var c in with.Ctes)
        {
            if (c.NodeCase != Node.NodeOneofCase.CommonTableExpr)
            {
                continue;
            }
            var cte = c.CommonTableExpr;
            if (!string.IsNullOrEmpty(cte.Ctename))
            {
                _cteNames.Add(cte.Ctename);
            }
            if (cte.Ctequery is not null)
            {
                WalkStatement(cte.Ctequery);
            }
        }
    }

    private void WalkFromItem(Node fromItem)
    {
        switch (fromItem.NodeCase)
        {
            case Node.NodeOneofCase.RangeVar:
                {
                    var rv = fromItem.RangeVar;
                    var name = rv.Relname ?? string.Empty;
                    // Skip CTE references — they're not real tables.
                    if (string.IsNullOrEmpty(rv.Schemaname) && _cteNames.Contains(name))
                    {
                        return;
                    }
                    _actions.Add(ParsedQueryAction.Of(
                        ActionKind.Read,
                        rv.ToObjectReference(ObjectKind.Table)));
                    break;
                }
            case Node.NodeOneofCase.JoinExpr:
                {
                    var j = fromItem.JoinExpr;
                    if (j.Larg is not null) { WalkFromItem(j.Larg); }
                    if (j.Rarg is not null) { WalkFromItem(j.Rarg); }
                    if (j.Quals is not null)
                    {
                        WalkExpression(j.Quals, ColumnUsage.Joined);
                    }
                    break;
                }
            case Node.NodeOneofCase.RangeSubselect:
                {
                    var rs = fromItem.RangeSubselect;
                    if (rs.Subquery is not null)
                    {
                        WalkStatement(rs.Subquery);
                    }
                    break;
                }
            case Node.NodeOneofCase.RangeFunction:
                // SRFs / table functions — skip (not a table reference).
                break;
            default:
                break;
        }
    }

    private void WalkExpression(Node expr, ColumnUsage usage)
    {
        switch (expr.NodeCase)
        {
            case Node.NodeOneofCase.ColumnRef:
                {
                    var qualifiers = expr.ColumnRef.ToColumnQualifiers();
                    if (qualifiers is { } q && q.Column != "*")
                    {
                        _columns.Add(new ColumnReference(q.Schema, q.Table, q.Column, usage));
                    }
                    break;
                }
            case Node.NodeOneofCase.AExpr:
                {
                    var ae = expr.AExpr;
                    if (ae.Lexpr is not null) { WalkExpression(ae.Lexpr, usage); }
                    if (ae.Rexpr is not null) { WalkExpression(ae.Rexpr, usage); }
                    break;
                }
            case Node.NodeOneofCase.BoolExpr:
                {
                    foreach (var arg in expr.BoolExpr.Args)
                    {
                        WalkExpression(arg, usage);
                    }
                    break;
                }
            case Node.NodeOneofCase.NullTest:
                if (expr.NullTest.Arg is not null) { WalkExpression(expr.NullTest.Arg, usage); }
                break;
            case Node.NodeOneofCase.BooleanTest:
                if (expr.BooleanTest.Arg is not null) { WalkExpression(expr.BooleanTest.Arg, usage); }
                break;
            case Node.NodeOneofCase.SubLink:
                {
                    var sl = expr.SubLink;
                    if (sl.Testexpr is not null) { WalkExpression(sl.Testexpr, usage); }
                    if (sl.Subselect is not null)
                    {
                        WalkStatement(sl.Subselect);
                    }
                    break;
                }
            case Node.NodeOneofCase.FuncCall:
                {
                    foreach (var a in expr.FuncCall.Args)
                    {
                        WalkExpression(a, usage);
                    }
                    break;
                }
            case Node.NodeOneofCase.CaseExpr:
                {
                    var ce = expr.CaseExpr;
                    if (ce.Arg is not null) { WalkExpression(ce.Arg, usage); }
                    foreach (var w in ce.Args)
                    {
                        if (w.NodeCase == Node.NodeOneofCase.CaseWhen)
                        {
                            if (w.CaseWhen.Expr is not null) { WalkExpression(w.CaseWhen.Expr, usage); }
                            if (w.CaseWhen.Result is not null) { WalkExpression(w.CaseWhen.Result, usage); }
                        }
                    }
                    if (ce.Defresult is not null) { WalkExpression(ce.Defresult, usage); }
                    break;
                }
            case Node.NodeOneofCase.CoalesceExpr:
                {
                    foreach (var a in expr.CoalesceExpr.Args) { WalkExpression(a, usage); }
                    break;
                }
            case Node.NodeOneofCase.List:
                {
                    foreach (var a in expr.List.Items) { WalkExpression(a, usage); }
                    break;
                }
            case Node.NodeOneofCase.TypeCast:
                if (expr.TypeCast.Arg is not null) { WalkExpression(expr.TypeCast.Arg, usage); }
                break;
            case Node.NodeOneofCase.AArrayExpr:
                foreach (var e in expr.AArrayExpr.Elements) { WalkExpression(e, usage); }
                break;
            case Node.NodeOneofCase.RowExpr:
                foreach (var e in expr.RowExpr.Args) { WalkExpression(e, usage); }
                break;
            default:
                break;
        }
    }
}
