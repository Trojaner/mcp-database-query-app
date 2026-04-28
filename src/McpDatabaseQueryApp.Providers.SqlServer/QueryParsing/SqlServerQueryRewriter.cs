using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;

/// <summary>
/// Applies <see cref="RewriteDirective"/>s to a previously-parsed T-SQL
/// batch by mutating the ScriptDom AST in place, then re-emits the SQL
/// via <see cref="Sql160ScriptGenerator"/>.
/// </summary>
public sealed class SqlServerQueryRewriter : IQueryRewriter
{
    public DatabaseKind Kind => DatabaseKind.SqlServer;

    public string Rewrite(ParsedBatch parsed, IReadOnlyList<RewriteDirective> directives)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(directives);

        if (directives.Count == 0)
        {
            return parsed.OriginalSql;
        }

        var handle = parsed.GetProviderState<TSqlAstHandle>();
        if (handle is null || handle.Script is null)
        {
            throw new QueryParseException(
                "Cannot rewrite: ParsedBatch does not carry a SQL Server AST. Did you parse with the wrong dialect?");
        }

        // Collect predicate directives (only kind we currently support).
        var predicates = new List<PredicateInjectionDirective>(directives.Count);
        foreach (var directive in directives)
        {
            switch (directive)
            {
                case PredicateInjectionDirective p:
                    predicates.Add(p);
                    break;
                default:
                    throw new QueryParseException(
                        $"Unsupported rewrite directive: {directive.GetType().Name}");
            }
        }

        if (predicates.Count > 0)
        {
            var injector = new PredicateInjector(predicates);
            handle.Script.Accept(injector);
        }

        var generator = new Sql160ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = true,
            NewLineBeforeFromClause = false,
            NewLineBeforeWhereClause = false,
            NewLineBeforeJoinClause = false,
            NewLineBeforeOrderByClause = false,
            NewLineBeforeGroupByClause = false,
        });
        generator.GenerateScript(handle.Script, out var output);
        return output;
    }

    /// <summary>
    /// Injects a parsed predicate fragment into every WHERE-bearing
    /// statement whose primary table matches one of the supplied
    /// directives.
    /// </summary>
    private sealed class PredicateInjector : TSqlFragmentVisitor
    {
        private readonly List<PredicateInjectionDirective> _directives;

        public PredicateInjector(List<PredicateInjectionDirective> directives)
        {
            _directives = directives;
        }

        public override void Visit(QuerySpecification node)
        {
            // Only inject for SELECTs whose FROM contains a matching table.
            var match = FindMatchingDirective(node.FromClause);
            if (match is not null)
            {
                node.WhereClause = MergeWhere(node.WhereClause, match.Predicate);
            }
        }

        public override void Visit(UpdateSpecification node)
        {
            if (node.Target is NamedTableReference t && TryMatch(t, out var match))
            {
                node.WhereClause = MergeWhere(node.WhereClause, match.Predicate);
            }
        }

        public override void Visit(DeleteSpecification node)
        {
            if (node.Target is NamedTableReference t && TryMatch(t, out var match))
            {
                node.WhereClause = MergeWhere(node.WhereClause, match.Predicate);
            }
        }

        private PredicateInjectionDirective? FindMatchingDirective(FromClause? from)
        {
            if (from is null)
            {
                return null;
            }
            foreach (var tableRef in EnumerateTableReferences(from.TableReferences))
            {
                if (tableRef is NamedTableReference named && TryMatch(named, out var match))
                {
                    return match;
                }
            }
            return null;
        }

        private static IEnumerable<TableReference> EnumerateTableReferences(IList<TableReference> refs)
        {
            foreach (var r in refs)
            {
                foreach (var item in EnumerateOne(r))
                {
                    yield return item;
                }
            }
        }

        private static IEnumerable<TableReference> EnumerateOne(TableReference r)
        {
            yield return r;
            if (r is QualifiedJoin qj)
            {
                if (qj.FirstTableReference is not null)
                {
                    foreach (var x in EnumerateOne(qj.FirstTableReference))
                    {
                        yield return x;
                    }
                }
                if (qj.SecondTableReference is not null)
                {
                    foreach (var x in EnumerateOne(qj.SecondTableReference))
                    {
                        yield return x;
                    }
                }
            }
        }

        private bool TryMatch(NamedTableReference named, out PredicateInjectionDirective match)
        {
            var refObj = named.SchemaObject.ToObjectReference(ObjectKind.Table);
            foreach (var d in _directives)
            {
                if (Matches(refObj, d.Target))
                {
                    match = d;
                    return true;
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
            if (target.Schema is not null && !string.Equals(candidate.Schema, target.Schema, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            if (target.Database is not null && !string.Equals(candidate.Database, target.Database, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }

        private static WhereClause MergeWhere(WhereClause? existing, string predicate)
        {
            var fragment = ParseBoolean(predicate);

            if (existing?.SearchCondition is null)
            {
                return new WhereClause { SearchCondition = fragment };
            }

            var combined = new BooleanBinaryExpression
            {
                BinaryExpressionType = BooleanBinaryExpressionType.And,
                FirstExpression = existing.SearchCondition,
                SecondExpression = new BooleanParenthesisExpression { Expression = fragment },
            };
            return new WhereClause { SearchCondition = combined };
        }

        private static BooleanExpression ParseBoolean(string predicate)
        {
            // Wrap the predicate in a SELECT so ScriptDom can parse it as a
            // boolean expression, then steal the WHERE clause.
            var sql = $"SELECT 1 WHERE {predicate}";
            var parser = new TSql160Parser(initialQuotedIdentifiers: true);
            using var reader = new StringReader(sql);
            var fragment = parser.Parse(reader, out var errors);
            if (errors is not null && errors.Count > 0)
            {
                throw new QueryParseException(
                    $"Invalid predicate '{predicate}': {errors[0].Message}");
            }
            if (fragment is TSqlScript script
                && script.Batches.Count > 0
                && script.Batches[0].Statements.Count > 0
                && script.Batches[0].Statements[0] is SelectStatement select
                && select.QueryExpression is QuerySpecification qs
                && qs.WhereClause?.SearchCondition is { } cond)
            {
                return cond;
            }
            throw new QueryParseException($"Could not extract boolean predicate from '{predicate}'.");
        }
    }
}
