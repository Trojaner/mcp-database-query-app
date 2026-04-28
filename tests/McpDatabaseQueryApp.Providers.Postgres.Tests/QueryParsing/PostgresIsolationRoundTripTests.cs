using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Providers.Postgres.QueryParsing;
using Xunit;

namespace McpDatabaseQueryApp.Providers.Postgres.Tests.QueryParsing;

/// <summary>
/// Cross-cutting check that the data-isolation engine + the live Postgres
/// rewriter produce predicate injections the libpg_query parser is happy
/// to round-trip.
/// </summary>
public sealed class PostgresIsolationRoundTripTests
{
    private readonly PostgresQueryParser _parser = new();
    private readonly PostgresQueryRewriter _rewriter = new();

    private static IsolationRule Rule(IsolationFilter filter, string schema = "public", string table = "events")
        => new(
            new IsolationRuleId(Guid.NewGuid().ToString("N")),
            ProfileId.Default,
            new IsolationScope("h", 5432, "d", schema, table),
            filter,
            IsolationRuleSource.Static,
            Priority: 0,
            Description: null);

    private List<RewriteDirective> BuildDirectives(ParsedBatch batch, params IsolationRule[] rules)
    {
        // Mirror IsolationRuleEngine: bind directive target schema to whatever
        // the parsed AST emitted (null when the SQL omits the schema), so the
        // rewriter's case-sensitive schema comparison still matches.
        var actualSchemas = batch.Statements
            .SelectMany(s => s.Actions)
            .Where(a => a.Target.Kind == ObjectKind.Table)
            .GroupBy(a => a.Target.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Target.Schema, StringComparer.OrdinalIgnoreCase);

        var ctx = new IsolationFilterContext();
        var directives = new List<RewriteDirective>();
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
            directives.Add(new PredicateInjectionDirective(target, predicate));
        }
        return directives;
    }

    [Fact]
    public void Equality_rule_appends_predicate_and_round_trips_through_parser()
    {
        var batch = _parser.Parse("SELECT * FROM events");
        var rule = Rule(new IsolationFilter.EqualityFilter("tenant_id", 42));
        var directives = BuildDirectives(batch, rule);

        var rewritten = _rewriter.Rewrite(batch, directives);

        rewritten.Should().Contain("tenant_id");
        rewritten.Should().ContainEquivalentOf("WHERE");

        var reparsed = _parser.Parse(rewritten);
        reparsed.Errors.Should().BeEmpty();
        reparsed.Statements[0].Actions.Should().Contain(
            a => a.Action == ActionKind.Read && a.Target.Name == "events");
    }

    [Fact]
    public void Equality_rule_AND_merges_with_existing_where_clause()
    {
        var batch = _parser.Parse("SELECT * FROM events WHERE active = true");
        var rule = Rule(new IsolationFilter.EqualityFilter("tenant_id", 7));
        var directives = BuildDirectives(batch, rule);

        var rewritten = _rewriter.Rewrite(batch, directives);

        rewritten.Should().Contain("active");
        rewritten.Should().Contain("tenant_id");
        rewritten.Should().ContainEquivalentOf("AND");

        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void InList_rule_emits_in_clause_that_round_trips()
    {
        var batch = _parser.Parse("SELECT * FROM events");
        var rule = Rule(new IsolationFilter.InListFilter(
            "region",
            new object?[] { "us", "eu" }));
        var directives = BuildDirectives(batch, rule);

        var rewritten = _rewriter.Rewrite(batch, directives);

        // The rewriter parses the predicate via libpg_query, which keeps
        // parameter references in the expression (e.g. @iso_0). Confirm the
        // IN clause survived the round-trip.
        rewritten.Should().ContainEquivalentOf("IN (");
        rewritten.Should().Contain("region");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void RawSql_rule_passes_predicate_through_unmodified()
    {
        var batch = _parser.Parse("SELECT * FROM events");
        var rule = Rule(new IsolationFilter.RawSqlFilter(
            "deleted_at IS NULL",
            new Dictionary<string, object?>()));
        var directives = BuildDirectives(batch, rule);

        var rewritten = _rewriter.Rewrite(batch, directives);

        rewritten.Should().Contain("deleted_at");
        rewritten.Should().ContainEquivalentOf("IS NULL");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }
}
