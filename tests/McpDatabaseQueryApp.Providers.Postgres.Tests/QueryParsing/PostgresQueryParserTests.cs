using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Providers.Postgres.QueryParsing;
using Xunit;

namespace McpDatabaseQueryApp.Providers.Postgres.Tests.QueryParsing;

public sealed class PostgresQueryParserTests
{
    private readonly PostgresQueryParser _parser = new();
    private readonly PostgresQueryRewriter _rewriter = new();

    [Fact]
    public void Kind_is_Postgres()
    {
        _parser.Kind.Should().Be(DatabaseKind.Postgres);
        _rewriter.Kind.Should().Be(DatabaseKind.Postgres);
    }

    [Fact]
    public void Simple_select_produces_one_read_action()
    {
        var batch = _parser.Parse("SELECT * FROM users");

        batch.Errors.Should().BeEmpty();
        batch.Statements.Should().HaveCount(1);
        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Select);
        stmt.IsMutation.Should().BeFalse();
        stmt.IsDestructive.Should().BeFalse();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Read);
        var read = stmt.Actions[0];
        read.Target.Name.Should().Be("users");
        read.Target.Schema.Should().BeNull();
    }

    [Fact]
    public void Multi_table_join_emits_one_read_per_table_and_marks_join_columns()
    {
        var batch = _parser.Parse(
            "SELECT u.id FROM users u JOIN orders o ON o.user_id = u.id");

        var stmt = batch.Statements[0];
        var reads = stmt.Actions.Where(a => a.Action == ActionKind.Read).ToList();
        reads.Should().HaveCount(2);
        reads.Select(r => r.Target.Name).Should().BeEquivalentTo(["users", "orders"]);

        var primary = stmt.Actions[0];
        primary.Columns.Should().Contain(c => c.Name == "user_id" && c.Usage == ColumnUsage.Joined);
        primary.Columns.Should().Contain(c => c.Name == "id" && c.Usage == ColumnUsage.Joined);
    }

    [Fact]
    public void Schema_qualified_objects_carry_schema()
    {
        var batch = _parser.Parse("SELECT * FROM public.users");

        var read = batch.Statements[0].Actions.Single(a => a.Action == ActionKind.Read);
        read.Target.Schema.Should().Be("public");
        read.Target.Name.Should().Be("users");
        read.Target.Database.Should().BeNull();
        read.Target.Server.Should().BeNull();
    }

    [Fact]
    public void Insert_statement_classifies_as_mutation_and_records_inserted_columns()
    {
        var batch = _parser.Parse("INSERT INTO t (a, b) VALUES (1, 2)");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Insert);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeFalse();
        var insert = stmt.Actions.Single(a => a.Action == ActionKind.Insert);
        insert.Target.Name.Should().Be("t");
        insert.Columns.Select(c => c.Name).Should().BeEquivalentTo(["a", "b"]);
        insert.Columns.Should().AllSatisfy(c => c.Usage.Should().Be(ColumnUsage.Inserted));
    }

    [Fact]
    public void Update_with_where_is_mutation_but_not_destructive()
    {
        var batch = _parser.Parse("UPDATE t SET a = 1 WHERE id = 2");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Update);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeFalse();
        var update = stmt.Actions.Single(a => a.Action == ActionKind.Update);
        update.Columns.Should().Contain(c => c.Name == "a" && c.Usage == ColumnUsage.Modified);
        update.Columns.Should().Contain(c => c.Name == "id" && c.Usage == ColumnUsage.Filtered);
    }

    [Fact]
    public void Update_without_where_is_destructive()
    {
        var batch = _parser.Parse("UPDATE t SET a = 1");

        batch.Statements[0].IsDestructive.Should().BeTrue();
    }

    [Fact]
    public void Delete_with_where_is_mutation_but_not_destructive()
    {
        var batch = _parser.Parse("DELETE FROM t WHERE id = 1");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Delete);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeFalse();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Delete);
    }

    [Fact]
    public void Delete_without_where_is_destructive()
    {
        var batch = _parser.Parse("DELETE FROM t");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Delete);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeTrue();
    }

    [Fact]
    public void Truncate_is_mutation_and_destructive()
    {
        var batch = _parser.Parse("TRUNCATE TABLE t");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Truncate);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeTrue();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Truncate);
    }

    [Fact]
    public void Drop_table_is_destructive()
    {
        var batch = _parser.Parse("DROP TABLE t");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.DropTable);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeTrue();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Drop);
    }

    [Fact]
    public void Create_table_is_mutation_but_not_destructive()
    {
        var batch = _parser.Parse("CREATE TABLE t (id int)");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.CreateTable);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeFalse();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Create);
    }

    [Fact]
    public void Cte_select_is_classified_as_select_and_finds_underlying_tables()
    {
        const string sql = "WITH x AS (SELECT 1 AS n) SELECT * FROM x JOIN users u ON u.id = x.n";

        var batch = _parser.Parse(sql);

        batch.Errors.Should().BeEmpty();
        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Select);
        var reads = stmt.Actions.Where(a => a.Action == ActionKind.Read).ToList();
        reads.Should().Contain(a => a.Target.Name == "users");
        // CTE name must NOT appear as a real table.
        reads.Should().NotContain(a => a.Target.Name == "x");
    }

    [Fact]
    public void Subquery_inside_where_is_walked()
    {
        const string sql = "SELECT * FROM a WHERE id IN (SELECT id FROM b)";

        var batch = _parser.Parse(sql);

        var reads = batch.Statements[0].Actions.Where(a => a.Action == ActionKind.Read).ToList();
        reads.Select(r => r.Target.Name).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Multiple_statements_separated_by_semicolons_yield_multiple_parsed_statements()
    {
        const string sql = "SELECT 1; INSERT INTO t (a) VALUES (1); DELETE FROM t WHERE id = 1";

        var batch = _parser.Parse(sql);

        batch.Statements.Should().HaveCount(3);
        batch.Statements.Select(s => s.StatementKind).Should().BeEquivalentTo(
            [StatementKind.Select, StatementKind.Insert, StatementKind.Delete],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void Syntax_errors_throw_QueryParseException()
    {
        Action act = () => _parser.Parse("SELEKT * FROM t");

        act.Should().Throw<QueryParseException>();
    }

    [Fact]
    public void Provider_ast_round_trips_and_is_tagged()
    {
        var batch = _parser.Parse("SELECT 1");

        batch.ProviderAst.Tag.Should().Be("pgquery");
        batch.ProviderAst.Value.Should().NotBeNull();
    }

    [Fact]
    public void Predicate_injection_round_trip_select()
    {
        var batch = _parser.Parse("SELECT * FROM users WHERE active = true");
        var directive = new PredicateInjectionDirective(
            new ObjectReference(ObjectKind.Table, null, null, null, "users"),
            "tenant_id = 1");

        var rewritten = _rewriter.Rewrite(batch, [directive]);

        rewritten.Should().Contain("tenant_id", "the directive's predicate must appear in the output");
        // The rewritten SQL should re-parse cleanly and still hit the
        // same table.
        var reparsed = _parser.Parse(rewritten);
        reparsed.Errors.Should().BeEmpty();
        reparsed.Statements[0].Actions.Should().Contain(
            a => a.Action == ActionKind.Read && a.Target.Name == "users");
    }

    [Fact]
    public void Predicate_injection_adds_where_when_missing()
    {
        var batch = _parser.Parse("SELECT id FROM users");
        var directive = new PredicateInjectionDirective(
            new ObjectReference(ObjectKind.Table, null, null, null, "users"),
            "tenant_id = 1");

        var rewritten = _rewriter.Rewrite(batch, [directive]);

        rewritten.Should().ContainEquivalentOf("WHERE");
        rewritten.Should().Contain("tenant_id");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Rewriter_passthrough_when_no_directives()
    {
        const string sql = "SELECT 1";
        var batch = _parser.Parse(sql);
        _rewriter.Rewrite(batch, []).Should().Be(sql);
    }
}
