using FluentAssertions;
using McpDatabaseQueryApp.Core.DataIsolation;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.DataIsolation;

public sealed class IsolationFilterTests
{
    [Fact]
    public void Equality_filter_emits_column_eq_named_parameter()
    {
        var filter = new IsolationFilter.EqualityFilter("tenant_id", 42);
        var ctx = new IsolationFilterContext();

        var (sql, parameters) = filter.ToPredicate(ctx);

        sql.Should().Be("tenant_id = @iso_0");
        parameters.Should().ContainSingle().Which.Key.Should().Be("iso_0");
        parameters["iso_0"].Should().Be(42);
    }

    [Fact]
    public void InList_filter_emits_in_clause_with_one_parameter_per_value()
    {
        var filter = new IsolationFilter.InListFilter("region", new object?[] { "us", "eu", "ap" });
        var ctx = new IsolationFilterContext();

        var (sql, parameters) = filter.ToPredicate(ctx);

        sql.Should().Be("region IN (@iso_0, @iso_1, @iso_2)");
        parameters.Should().HaveCount(3);
        parameters.Values.Should().BeEquivalentTo(new object?[] { "us", "eu", "ap" });
    }

    [Fact]
    public void InList_filter_with_empty_list_emits_always_false_predicate()
    {
        var filter = new IsolationFilter.InListFilter("region", Array.Empty<object?>());
        var ctx = new IsolationFilterContext();

        var (sql, parameters) = filter.ToPredicate(ctx);

        sql.Should().Be("1 = 0");
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void RawSql_filter_passes_predicate_and_parameters_through_verbatim()
    {
        var filter = new IsolationFilter.RawSqlFilter(
            "deleted_at IS NULL AND tenant_id = @tenant",
            new Dictionary<string, object?> { ["tenant"] = 7 });
        var ctx = new IsolationFilterContext();

        var (sql, parameters) = filter.ToPredicate(ctx);

        sql.Should().Be("deleted_at IS NULL AND tenant_id = @tenant");
        parameters.Should().ContainKey("tenant").WhoseValue.Should().Be(7);
    }

    [Fact]
    public void Allocator_returns_distinct_names_across_filters_sharing_a_context()
    {
        var ctx = new IsolationFilterContext();
        var first = new IsolationFilter.EqualityFilter("a", 1).ToPredicate(ctx);
        var second = new IsolationFilter.EqualityFilter("b", 2).ToPredicate(ctx);

        first.Sql.Should().Contain("@iso_0");
        second.Sql.Should().Contain("@iso_1");
        first.Parameters.Keys.Intersect(second.Parameters.Keys).Should().BeEmpty();
    }

    [Fact]
    public void Equality_filter_rejects_invalid_column_identifiers()
    {
        var filter = new IsolationFilter.EqualityFilter("DROP TABLE", 1);
        var ctx = new IsolationFilterContext();

        Action act = () => filter.ToPredicate(ctx);

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Allocator_prefix_can_be_overridden()
    {
        var ctx = new IsolationFilterContext("p");
        var (sql, parameters) = new IsolationFilter.EqualityFilter("c", 9).ToPredicate(ctx);

        sql.Should().Be("c = @p_0");
        parameters.Should().ContainKey("p_0");
    }
}
