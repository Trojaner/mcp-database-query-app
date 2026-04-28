using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryParsing;
using PgSqlParser;

namespace McpDatabaseQueryApp.Providers.Postgres.QueryParsing;

/// <summary>
/// Applies <see cref="RewriteDirective"/>s to a parsed PostgreSQL batch by
/// mutating the libpg_query protobuf AST in place, then re-emits SQL via
/// <see cref="Parser.Deparse"/>.
/// </summary>
public sealed class PostgresQueryRewriter : IQueryRewriter
{
    /// <inheritdoc />
    public DatabaseKind Kind => DatabaseKind.Postgres;

    /// <inheritdoc />
    public string Rewrite(ParsedBatch parsed, IReadOnlyList<RewriteDirective> directives)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(directives);

        if (directives.Count == 0)
        {
            return parsed.OriginalSql;
        }

        var handle = parsed.GetProviderState<PgQueryAstHandle>();
        if (handle is null)
        {
            throw new QueryParseException(
                "Cannot rewrite: ParsedBatch does not carry a Postgres AST. Did you parse with the wrong dialect?");
        }

        var predicates = new List<PredicateInjectionDirective>(directives.Count);
        foreach (var d in directives)
        {
            switch (d)
            {
                case PredicateInjectionDirective p:
                    predicates.Add(p);
                    break;
                default:
                    throw new QueryParseException(
                        $"Unsupported rewrite directive: {d.GetType().Name}");
            }
        }

        if (predicates.Count > 0)
        {
            foreach (var raw in handle.ParseResult.Stmts)
            {
                ApplyPredicates(raw.Stmt, predicates);
            }
        }

        var deparsed = Parser.Deparse(handle.ParseResult);
        if (!deparsed.IsSuccess || deparsed.Value is null)
        {
            throw new QueryParseException(
                $"Failed to deparse rewritten AST: {deparsed.Error?.Message ?? "unknown"}");
        }
        return deparsed.Value;
    }

    private static void ApplyPredicates(Node stmt, List<PredicateInjectionDirective> directives)
    {
        switch (stmt.NodeCase)
        {
            case Node.NodeOneofCase.SelectStmt:
                ApplyToSelect(stmt.SelectStmt, directives);
                break;
            case Node.NodeOneofCase.UpdateStmt:
                {
                    var u = stmt.UpdateStmt;
                    if (TryMatch(u.Relation, directives, out var pred))
                    {
                        u.WhereClause = MergeWhere(u.WhereClause, pred);
                    }
                    break;
                }
            case Node.NodeOneofCase.DeleteStmt:
                {
                    var d = stmt.DeleteStmt;
                    if (TryMatch(d.Relation, directives, out var pred))
                    {
                        d.WhereClause = MergeWhere(d.WhereClause, pred);
                    }
                    break;
                }
            default:
                break;
        }
    }

    private static void ApplyToSelect(SelectStmt select, List<PredicateInjectionDirective> directives)
    {
        // Recurse into CTEs, set-op arms, and subqueries inside FROM so
        // nested SELECTs against the same target also get their predicate.
        if (select.WithClause is not null)
        {
            foreach (var c in select.WithClause.Ctes)
            {
                if (c.NodeCase == Node.NodeOneofCase.CommonTableExpr
                    && c.CommonTableExpr.Ctequery is { } q)
                {
                    ApplyPredicates(q, directives);
                }
            }
        }
        if (select.Larg is not null) { ApplyToSelect(select.Larg, directives); }
        if (select.Rarg is not null) { ApplyToSelect(select.Rarg, directives); }

        foreach (var f in select.FromClause)
        {
            ApplyToFromItem(f, directives);
        }

        // Match against any RangeVar in the FROM clause (top-level or
        // beneath joins).
        if (TryFindFromMatch(select.FromClause, directives, out var match))
        {
            select.WhereClause = MergeWhere(select.WhereClause, match);
        }
    }

    private static void ApplyToFromItem(Node item, List<PredicateInjectionDirective> directives)
    {
        switch (item.NodeCase)
        {
            case Node.NodeOneofCase.RangeSubselect:
                if (item.RangeSubselect.Subquery is { } sq)
                {
                    ApplyPredicates(sq, directives);
                }
                break;
            case Node.NodeOneofCase.JoinExpr:
                {
                    var j = item.JoinExpr;
                    if (j.Larg is not null) { ApplyToFromItem(j.Larg, directives); }
                    if (j.Rarg is not null) { ApplyToFromItem(j.Rarg, directives); }
                    break;
                }
        }
    }

    private static bool TryFindFromMatch(
        Google.Protobuf.Collections.RepeatedField<Node> fromClause,
        List<PredicateInjectionDirective> directives,
        out PredicateInjectionDirective match)
    {
        foreach (var f in fromClause)
        {
            if (TryFindFromMatchInItem(f, directives, out match))
            {
                return true;
            }
        }
        match = null!;
        return false;
    }

    private static bool TryFindFromMatchInItem(Node item, List<PredicateInjectionDirective> directives, out PredicateInjectionDirective match)
    {
        switch (item.NodeCase)
        {
            case Node.NodeOneofCase.RangeVar:
                return TryMatch(item.RangeVar, directives, out match);
            case Node.NodeOneofCase.JoinExpr:
                {
                    var j = item.JoinExpr;
                    if (j.Larg is not null && TryFindFromMatchInItem(j.Larg, directives, out match))
                    {
                        return true;
                    }
                    if (j.Rarg is not null && TryFindFromMatchInItem(j.Rarg, directives, out match))
                    {
                        return true;
                    }
                    break;
                }
        }
        match = null!;
        return false;
    }

    private static bool TryMatch(RangeVar? rv, List<PredicateInjectionDirective> directives, out PredicateInjectionDirective match)
    {
        if (rv is not null)
        {
            var refObj = rv.ToObjectReference(ObjectKind.Table);
            foreach (var d in directives)
            {
                if (Matches(refObj, d.Target))
                {
                    match = d;
                    return true;
                }
            }
        }
        match = null!;
        return false;
    }

    private static bool Matches(ObjectReference candidate, ObjectReference target)
    {
        if (!string.Equals(candidate.Name, target.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (target.Schema is not null
            && !string.Equals(candidate.Schema, target.Schema, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (target.Database is not null
            && !string.Equals(candidate.Database, target.Database, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    private static Node MergeWhere(Node? existing, PredicateInjectionDirective directive)
    {
        var predicateNode = ParsePredicate(directive.Predicate);
        if (existing is null)
        {
            return predicateNode;
        }
        var combined = new Node
        {
            BoolExpr = new BoolExpr { Boolop = BoolExprType.AndExpr },
        };
        combined.BoolExpr.Args.Add(existing);
        combined.BoolExpr.Args.Add(predicateNode);
        return combined;
    }

    /// <summary>
    /// Parses a raw boolean predicate by wrapping it in a SELECT and
    /// stealing the resulting WHERE expression.
    /// </summary>
    private static Node ParsePredicate(string predicate)
    {
        var sql = $"SELECT 1 WHERE {predicate}";
        var result = Parser.Parse(sql, ParserOptions.Default);
        if (!result.IsSuccess || result.Value is null)
        {
            throw new QueryParseException(
                $"Invalid predicate '{predicate}': {result.Error?.Message ?? "parse error"}");
        }
        var stmts = result.Value.Stmts;
        if (stmts.Count == 0
            || stmts[0].Stmt.NodeCase != Node.NodeOneofCase.SelectStmt
            || stmts[0].Stmt.SelectStmt.WhereClause is not { } where)
        {
            throw new QueryParseException(
                $"Could not extract boolean predicate from '{predicate}'.");
        }
        return where;
    }
}
