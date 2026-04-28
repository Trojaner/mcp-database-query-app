using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;

/// <summary>
/// Walks one <see cref="TSqlStatement"/> and produces the matching list of
/// <see cref="ParsedQueryAction"/>s. Implemented as a
/// <see cref="TSqlFragmentVisitor"/> (visitor pattern). The visitor relies
/// on default ScriptDom traversal — overrides only add behaviour and use
/// <c>ExplicitVisit</c> to manage scope flags around children.
/// </summary>
internal sealed class TSqlActionExtractor : TSqlFragmentVisitor
{
    private readonly List<ParsedQueryAction> _actions = [];
    private readonly List<ColumnReference> _columns = [];

    private bool _inWhereScope;
    private bool _inJoinScope;
    private bool _inOrderScope;
    private bool _inGroupScope;
    private bool _inSetScope;

    public (IReadOnlyList<ParsedQueryAction> Actions, IReadOnlyList<ColumnReference> Columns) Extract(TSqlStatement statement)
    {
        statement.Accept(this);

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

    // ---- DML targets --------------------------------------------------

    public override void ExplicitVisit(InsertStatement node)
    {
        var spec = node.InsertSpecification;
        if (spec?.Target is NamedTableReference named)
        {
            var target = named.SchemaObject.ToObjectReference(ObjectKind.Table);
            var insertedColumns = (spec.Columns ?? [])
                .Select(c => new ColumnReference(target.Schema, target.Name, c.ColumnName() ?? string.Empty, ColumnUsage.Inserted))
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .ToList();
            _actions.Add(new ParsedQueryAction(ActionKind.Insert, target, insertedColumns));
        }

        // Walk the source SELECT (if any) so we capture reads.
        if (spec?.InsertSource is SelectInsertSource selectInsert)
        {
            selectInsert.Select?.Accept(this);
        }
    }

    public override void ExplicitVisit(UpdateStatement node)
    {
        var spec = node.UpdateSpecification;
        if (spec?.Target is NamedTableReference named)
        {
            var target = named.SchemaObject.ToObjectReference(ObjectKind.Table);
            var modified = new List<ColumnReference>();
            foreach (var clause in spec.SetClauses ?? [])
            {
                if (clause is AssignmentSetClause asgn && asgn.Column is { } col)
                {
                    var n = col.ColumnName();
                    if (!string.IsNullOrEmpty(n))
                    {
                        modified.Add(new ColumnReference(target.Schema, target.Name, n, ColumnUsage.Modified));
                    }
                }
            }
            _actions.Add(new ParsedQueryAction(ActionKind.Update, target, modified));
        }

        // Walk SET expressions (right-hand sides), FROM, WHERE.
        if (spec is null) { return; }

        _inSetScope = true;
        foreach (var clause in spec.SetClauses ?? [])
        {
            if (clause is AssignmentSetClause asgn && asgn.NewValue is not null)
            {
                asgn.NewValue.Accept(this);
            }
        }
        _inSetScope = false;

        spec.FromClause?.Accept(this);
        if (spec.WhereClause is not null)
        {
            _inWhereScope = true;
            spec.WhereClause.Accept(this);
            _inWhereScope = false;
        }
    }

    public override void ExplicitVisit(DeleteStatement node)
    {
        var spec = node.DeleteSpecification;
        if (spec?.Target is NamedTableReference named)
        {
            var target = named.SchemaObject.ToObjectReference(ObjectKind.Table);
            _actions.Add(new ParsedQueryAction(ActionKind.Delete, target, []));
        }

        spec?.FromClause?.Accept(this);
        if (spec?.WhereClause is not null)
        {
            _inWhereScope = true;
            spec.WhereClause.Accept(this);
            _inWhereScope = false;
        }
    }

    public override void ExplicitVisit(MergeStatement node)
    {
        var spec = node.MergeSpecification;
        if (spec?.Target is NamedTableReference target)
        {
            var t = target.SchemaObject.ToObjectReference(ObjectKind.Table);
            _actions.Add(new ParsedQueryAction(ActionKind.Update, t, []));
        }
        if (spec?.TableReference is NamedTableReference source)
        {
            var s = source.SchemaObject.ToObjectReference(ObjectKind.Table);
            _actions.Add(new ParsedQueryAction(ActionKind.Read, s, []));
        }
    }

    public override void ExplicitVisit(TruncateTableStatement node)
    {
        var t = node.TableName.ToObjectReference(ObjectKind.Table);
        _actions.Add(new ParsedQueryAction(ActionKind.Truncate, t, []));
    }

    // ---- DDL ----------------------------------------------------------

    public override void ExplicitVisit(CreateTableStatement node)
    {
        var t = node.SchemaObjectName.ToObjectReference(ObjectKind.Table);
        _actions.Add(new ParsedQueryAction(ActionKind.Create, t, []));
    }

    public override void ExplicitVisit(DropTableStatement node)
    {
        foreach (var name in node.Objects ?? [])
        {
            _actions.Add(new ParsedQueryAction(
                ActionKind.Drop,
                name.ToObjectReference(ObjectKind.Table),
                []));
        }
    }

    public override void ExplicitVisit(AlterTableAddTableElementStatement node)
        => _actions.Add(new ParsedQueryAction(ActionKind.Alter, node.SchemaObjectName.ToObjectReference(ObjectKind.Table), []));

    public override void ExplicitVisit(AlterTableAlterColumnStatement node)
        => _actions.Add(new ParsedQueryAction(ActionKind.Alter, node.SchemaObjectName.ToObjectReference(ObjectKind.Table), []));

    public override void ExplicitVisit(AlterTableConstraintModificationStatement node)
        => _actions.Add(new ParsedQueryAction(ActionKind.Alter, node.SchemaObjectName.ToObjectReference(ObjectKind.Table), []));

    public override void ExplicitVisit(AlterTableDropTableElementStatement node)
        => _actions.Add(new ParsedQueryAction(ActionKind.Alter, node.SchemaObjectName.ToObjectReference(ObjectKind.Table), []));

    public override void ExplicitVisit(AlterTableSetStatement node)
        => _actions.Add(new ParsedQueryAction(ActionKind.Alter, node.SchemaObjectName.ToObjectReference(ObjectKind.Table), []));

    public override void ExplicitVisit(AlterTableSwitchStatement node)
        => _actions.Add(new ParsedQueryAction(ActionKind.Alter, node.SchemaObjectName.ToObjectReference(ObjectKind.Table), []));

    public override void ExplicitVisit(CreateViewStatement node)
    {
        var v = node.SchemaObjectName.ToObjectReference(ObjectKind.View);
        _actions.Add(new ParsedQueryAction(ActionKind.Create, v, []));
        node.SelectStatement?.Accept(this);
    }

    public override void ExplicitVisit(AlterViewStatement node)
    {
        var v = node.SchemaObjectName.ToObjectReference(ObjectKind.View);
        _actions.Add(new ParsedQueryAction(ActionKind.Alter, v, []));
        node.SelectStatement?.Accept(this);
    }

    public override void ExplicitVisit(DropViewStatement node)
    {
        foreach (var name in node.Objects ?? [])
        {
            _actions.Add(new ParsedQueryAction(
                ActionKind.Drop,
                name.ToObjectReference(ObjectKind.View),
                []));
        }
    }

    public override void ExplicitVisit(CreateIndexStatement node)
    {
        var ixName = node.Name?.Value ?? string.Empty;
        var indexRef = new ObjectReference(ObjectKind.Index, null, null, null, ixName);
        _actions.Add(new ParsedQueryAction(ActionKind.Create, indexRef, []));
        if (node.OnName is not null)
        {
            var t = node.OnName.ToObjectReference(ObjectKind.Table);
            _actions.Add(new ParsedQueryAction(ActionKind.Alter, t, []));
        }
    }

    public override void ExplicitVisit(DropIndexStatement node)
    {
        var indexRef = new ObjectReference(ObjectKind.Index, null, null, null, "index");
        _actions.Add(new ParsedQueryAction(ActionKind.Drop, indexRef, []));
    }

    public override void ExplicitVisit(CreateProcedureStatement node)
    {
        var p = node.ProcedureReference?.Name?.ToObjectReference(ObjectKind.Procedure)
            ?? new ObjectReference(ObjectKind.Procedure, null, null, null, string.Empty);
        _actions.Add(new ParsedQueryAction(ActionKind.Create, p, []));
    }

    public override void ExplicitVisit(AlterProcedureStatement node)
    {
        var p = node.ProcedureReference?.Name?.ToObjectReference(ObjectKind.Procedure)
            ?? new ObjectReference(ObjectKind.Procedure, null, null, null, string.Empty);
        _actions.Add(new ParsedQueryAction(ActionKind.Alter, p, []));
    }

    public override void ExplicitVisit(DropProcedureStatement node)
    {
        foreach (var n in node.Objects ?? [])
        {
            _actions.Add(new ParsedQueryAction(ActionKind.Drop, n.ToObjectReference(ObjectKind.Procedure), []));
        }
    }

    public override void ExplicitVisit(CreateFunctionStatement node)
    {
        var name = node.Name?.ToObjectReference(ObjectKind.Function)
            ?? new ObjectReference(ObjectKind.Function, null, null, null, string.Empty);
        _actions.Add(new ParsedQueryAction(ActionKind.Create, name, []));
    }

    public override void ExplicitVisit(AlterFunctionStatement node)
    {
        var name = node.Name?.ToObjectReference(ObjectKind.Function)
            ?? new ObjectReference(ObjectKind.Function, null, null, null, string.Empty);
        _actions.Add(new ParsedQueryAction(ActionKind.Alter, name, []));
    }

    public override void ExplicitVisit(DropFunctionStatement node)
    {
        foreach (var n in node.Objects ?? [])
        {
            _actions.Add(new ParsedQueryAction(ActionKind.Drop, n.ToObjectReference(ObjectKind.Function), []));
        }
    }

    public override void ExplicitVisit(GrantStatement node)
    {
        var subject = node.SecurityTargetObject?.ObjectName?.MultiPartIdentifier;
        if (subject is not null && subject.Identifiers.Count > 0)
        {
            var name = subject.Identifiers[^1].Value;
            string? schema = subject.Identifiers.Count > 1 ? subject.Identifiers[^2].Value : null;
            _actions.Add(new ParsedQueryAction(
                ActionKind.Grant,
                new ObjectReference(ObjectKind.Other, null, null, schema, name),
                []));
        }
    }

    public override void ExplicitVisit(RevokeStatement node)
    {
        var subject = node.SecurityTargetObject?.ObjectName?.MultiPartIdentifier;
        if (subject is not null && subject.Identifiers.Count > 0)
        {
            var name = subject.Identifiers[^1].Value;
            string? schema = subject.Identifiers.Count > 1 ? subject.Identifiers[^2].Value : null;
            _actions.Add(new ParsedQueryAction(
                ActionKind.Revoke,
                new ObjectReference(ObjectKind.Other, null, null, schema, name),
                []));
        }
    }

    public override void ExplicitVisit(ExecuteStatement node)
    {
        if (node.ExecuteSpecification?.ExecutableEntity is ExecutableProcedureReference pr
            && pr.ProcedureReference?.ProcedureReference?.Name is { } name)
        {
            _actions.Add(new ParsedQueryAction(
                ActionKind.Execute,
                name.ToObjectReference(ObjectKind.Procedure),
                []));
        }
        else
        {
            _actions.Add(new ParsedQueryAction(
                ActionKind.Execute,
                new ObjectReference(ObjectKind.Procedure, null, null, null, "?"),
                []));
        }
    }

    // ---- Scope tracking via ExplicitVisit -----------------------------

    public override void ExplicitVisit(WhereClause node)
    {
        var prev = _inWhereScope;
        _inWhereScope = true;
        try
        {
            base.ExplicitVisit(node);
        }
        finally
        {
            _inWhereScope = prev;
        }
    }

    public override void ExplicitVisit(GroupByClause node)
    {
        var prev = _inGroupScope;
        _inGroupScope = true;
        try
        {
            base.ExplicitVisit(node);
        }
        finally
        {
            _inGroupScope = prev;
        }
    }

    public override void ExplicitVisit(OrderByClause node)
    {
        var prev = _inOrderScope;
        _inOrderScope = true;
        try
        {
            base.ExplicitVisit(node);
        }
        finally
        {
            _inOrderScope = prev;
        }
    }

    public override void ExplicitVisit(QualifiedJoin node)
    {
        // Visit the table refs without join scope, then turn the scope on
        // for the search condition only.
        node.FirstTableReference?.Accept(this);
        node.SecondTableReference?.Accept(this);
        if (node.SearchCondition is not null)
        {
            var prev = _inJoinScope;
            _inJoinScope = true;
            try
            {
                node.SearchCondition.Accept(this);
            }
            finally
            {
                _inJoinScope = prev;
            }
        }
    }

    // ---- Targeted overrides used during default traversal -------------

    public override void ExplicitVisit(NamedTableReference node)
    {
        // Default traversal will reach here from FROM clauses, joins,
        // CTE definitions, etc. We treat all such occurrences as Read.
        var target = node.SchemaObject.ToObjectReference(ObjectKind.Table);
        _actions.Add(new ParsedQueryAction(ActionKind.Read, target, []));
        // No need to walk further — ScriptDom NamedTableReference children
        // are alias / temporal hints, not nested queries.
    }

    public override void ExplicitVisit(ColumnReferenceExpression node)
    {
        var name = node.ColumnName();
        if (string.IsNullOrEmpty(name) || name == "*")
        {
            return;
        }
        var (schema, table) = node.ColumnQualifiers();
        ColumnUsage usage;
        if (_inSetScope)
        {
            usage = ColumnUsage.Modified;
        }
        else if (_inJoinScope)
        {
            usage = ColumnUsage.Joined;
        }
        else if (_inWhereScope)
        {
            usage = ColumnUsage.Filtered;
        }
        else if (_inOrderScope)
        {
            usage = ColumnUsage.OrderedBy;
        }
        else if (_inGroupScope)
        {
            usage = ColumnUsage.GroupedBy;
        }
        else
        {
            usage = ColumnUsage.Projected;
        }
        _columns.Add(new ColumnReference(schema, table, name, usage));
    }
}
