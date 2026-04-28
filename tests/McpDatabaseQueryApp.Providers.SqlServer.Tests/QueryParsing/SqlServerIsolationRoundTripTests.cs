using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;
using Xunit;

namespace McpDatabaseQueryApp.Providers.SqlServer.Tests.QueryParsing;

/// <summary>
/// Verifies that the data-isolation engine + the live SQL Server rewriter
/// produce predicate injections ScriptDom is willing to round-trip.
/// </summary>
public sealed class SqlServerIsolationRoundTripTests
{
    private readonly SqlServerQueryParser _parser = new();
    private readonly SqlServerQueryRewriter _rewriter = new();

    private static IsolationRule Rule(IsolationFilter filter, string schema = "dbo", string table = "Events")
        => new(
            new IsolationRuleId(Guid.NewGuid().ToString("N")),
            ProfileId.Default,
            new IsolationScope("h", 1433, "d", schema, table),
            filter,
            IsolationRuleSource.Static,
            Priority: 0,
            Description: null);

    private static List<RewriteDirective> BuildDirectives(ParsedBatch batch, params IsolationRule[] rules)
    {
        var actualSchemas = batch.Statements
            .SelectMany(s => s.Actions)
            .Where(a => a.Target.Kind == ObjectKind.Table)
            .GroupBy(a => a.Target.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Target.Schema, StringComparer.OrdinalIgnoreCase);

        var ctx = new IsolationFilterContext();
        var list = new List<RewriteDirective>();
        foreach (var rule in rules)
        {
            var (predicate, _) = rule.Filter.ToPredicate(ctx);
            actualSchemas.TryGetValue(rule.Scope.Table, out var actualSchema);
            var target = new ObjectReference(
                ObjectKind.Table,
                Server: null,
                Database: null,
                Schema: actualSchema,
                Name: rule.Scope.Table);
            list.Add(new PredicateInjectionDirective(target, predicate));
        }
        return list;
    }

    [Fact]
    public void Equality_rule_appends_predicate_and_round_trips()
    {
        var batch = _parser.Parse("SELECT * FROM dbo.Events");
        var directives = BuildDirectives(batch, Rule(new IsolationFilter.EqualityFilter("tenant_id", 42)));

        var rewritten = _rewriter.Rewrite(batch, directives);

        rewritten.Should().Contain("tenant_id");
        rewritten.Should().ContainEquivalentOf("WHERE");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Equality_rule_AND_merges_with_existing_where_clause()
    {
        var batch = _parser.Parse("SELECT * FROM dbo.Events WHERE active = 1");
        var directives = BuildDirectives(batch, Rule(new IsolationFilter.EqualityFilter("tenant_id", 7)));

        var rewritten = _rewriter.Rewrite(batch, directives);

        rewritten.Should().Contain("active");
        rewritten.Should().Contain("tenant_id");
        rewritten.Should().ContainEquivalentOf("AND");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void InList_rule_emits_in_clause_that_round_trips()
    {
        var batch = _parser.Parse("SELECT * FROM dbo.Events");
        var directives = BuildDirectives(batch, Rule(new IsolationFilter.InListFilter(
            "region",
            new object?[] { "us", "eu" })));

        var rewritten = _rewriter.Rewrite(batch, directives);

        rewritten.Should().ContainEquivalentOf("IN (");
        rewritten.Should().Contain("region");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void RawSql_rule_passes_predicate_through_unmodified()
    {
        var batch = _parser.Parse("SELECT * FROM dbo.Events");
        var directives = BuildDirectives(batch, Rule(new IsolationFilter.RawSqlFilter(
            "deleted_at IS NULL",
            new Dictionary<string, object?>())));

        var rewritten = _rewriter.Rewrite(batch, directives);

        rewritten.Should().Contain("deleted_at");
        rewritten.Should().ContainEquivalentOf("IS NULL");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }
}
