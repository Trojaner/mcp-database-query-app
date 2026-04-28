using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;
using Xunit;

namespace McpDatabaseQueryApp.Providers.SqlServer.Tests.QueryParsing;

public sealed class SqlServerQueryParserTests
{
    private readonly SqlServerQueryParser _parser = new();
    private readonly SqlServerQueryRewriter _rewriter = new();

    [Fact]
    public void Kind_is_SqlServer()
    {
        _parser.Kind.Should().Be(DatabaseKind.SqlServer);
        _rewriter.Kind.Should().Be(DatabaseKind.SqlServer);
    }

    [Fact]
    public void Simple_select_produces_one_read_action()
    {
        var batch = _parser.Parse("SELECT id, name FROM dbo.Users;");

        batch.Errors.Should().BeEmpty();
        batch.Statements.Should().HaveCount(1);
        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Select);
        stmt.IsMutation.Should().BeFalse();
        stmt.IsDestructive.Should().BeFalse();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Read);
        stmt.Actions[0].Target.Schema.Should().Be("dbo");
        stmt.Actions[0].Target.Name.Should().Be("Users");
    }

    [Fact]
    public void Multi_table_join_emits_one_read_per_table()
    {
        const string sql = """
            SELECT u.id, p.title
            FROM dbo.Users u
            INNER JOIN dbo.Posts p ON p.user_id = u.id;
            """;

        var batch = _parser.Parse(sql);

        var reads = batch.Statements[0].Actions.Where(a => a.Action == ActionKind.Read).ToList();
        reads.Should().HaveCount(2);
        reads.Select(r => r.Target.Name).Should().Contain(["Users", "Posts"]);
    }

    [Fact]
    public void Insert_statement_classifies_as_mutation()
    {
        var batch = _parser.Parse("INSERT INTO dbo.Users (id, name) VALUES (1, 'a');");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Insert);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeFalse();
        var insertAction = stmt.Actions.Single(a => a.Action == ActionKind.Insert);
        insertAction.Columns.Select(c => c.Name).Should().BeEquivalentTo(["id", "name"]);
        insertAction.Columns.Should().AllSatisfy(c => c.Usage.Should().Be(ColumnUsage.Inserted));
    }

    [Fact]
    public void Update_with_where_is_not_destructive()
    {
        var batch = _parser.Parse("UPDATE dbo.Users SET name = 'x' WHERE id = 1;");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Update);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeFalse();
        stmt.Actions.Should().Contain(a => a.Action == ActionKind.Update);
    }

    [Fact]
    public void Update_without_where_is_destructive()
    {
        var batch = _parser.Parse("UPDATE dbo.Users SET name = 'x';");

        batch.Statements[0].IsDestructive.Should().BeTrue();
    }

    [Fact]
    public void Delete_without_where_is_destructive()
    {
        var batch = _parser.Parse("DELETE FROM dbo.Users;");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Delete);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeTrue();
    }

    [Fact]
    public void Merge_statement_emits_update_and_read_actions()
    {
        const string sql = """
            MERGE INTO dbo.Target AS T
            USING dbo.Source AS S
            ON T.id = S.id
            WHEN MATCHED THEN UPDATE SET T.value = S.value;
            """;
        var batch = _parser.Parse(sql);

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Merge);
        stmt.IsMutation.Should().BeTrue();
        stmt.Actions.Select(a => a.Action).Should().Contain([ActionKind.Update, ActionKind.Read]);
    }

    [Fact]
    public void Truncate_is_mutation_and_destructive()
    {
        var batch = _parser.Parse("TRUNCATE TABLE dbo.Logs;");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Truncate);
        stmt.IsMutation.Should().BeTrue();
        stmt.IsDestructive.Should().BeTrue();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Truncate);
    }

    [Fact]
    public void Drop_table_is_destructive()
    {
        var batch = _parser.Parse("DROP TABLE dbo.Logs;");

        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.DropTable);
        stmt.IsDestructive.Should().BeTrue();
        stmt.Actions.Should().ContainSingle(a => a.Action == ActionKind.Drop);
    }

    [Fact]
    public void Cte_select_is_classified_as_select_and_finds_underlying_tables()
    {
        const string sql = """
            WITH ActiveUsers AS (
                SELECT id FROM dbo.Users WHERE active = 1
            )
            SELECT u.id FROM ActiveUsers u;
            """;
        var batch = _parser.Parse(sql);

        batch.Errors.Should().BeEmpty();
        var stmt = batch.Statements[0];
        stmt.StatementKind.Should().Be(StatementKind.Select);
        stmt.Actions.Should().Contain(a => a.Action == ActionKind.Read && a.Target.Name == "Users");
    }

    [Fact]
    public void Subquery_inside_where_is_walked()
    {
        const string sql = """
            SELECT id FROM dbo.Users
            WHERE id IN (SELECT user_id FROM dbo.Bans);
            """;
        var batch = _parser.Parse(sql);

        var reads = batch.Statements[0].Actions.Where(a => a.Action == ActionKind.Read).ToList();
        reads.Select(r => r.Target.Name).Should().Contain(["Users", "Bans"]);
    }

    [Fact]
    public void Schema_qualified_objects_carry_schema()
    {
        var batch = _parser.Parse("SELECT * FROM sales.orders;");

        var read = batch.Statements[0].Actions.Single(a => a.Action == ActionKind.Read);
        read.Target.Schema.Should().Be("sales");
        read.Target.Name.Should().Be("orders");
    }

    [Fact]
    public void Three_part_names_capture_database()
    {
        var batch = _parser.Parse("SELECT * FROM mydb.dbo.Users;");

        var read = batch.Statements[0].Actions.Single(a => a.Action == ActionKind.Read);
        read.Target.Database.Should().Be("mydb");
        read.Target.Schema.Should().Be("dbo");
        read.Target.Name.Should().Be("Users");
    }

    [Fact]
    public void Multiple_statements_separated_by_semicolons_yield_multiple_parsed_statements()
    {
        const string sql = """
            SELECT 1;
            INSERT INTO dbo.Users (id) VALUES (1);
            DELETE FROM dbo.Users WHERE id = 1;
            """;

        var batch = _parser.Parse(sql);

        batch.Statements.Should().HaveCount(3);
        batch.Statements.Select(s => s.StatementKind).Should().BeEquivalentTo(
            [StatementKind.Select, StatementKind.Insert, StatementKind.Delete],
            o => o.WithStrictOrdering());
    }

    [Fact]
    public void Multiple_batches_separated_by_GO_are_handled()
    {
        // ScriptDom recognizes GO as a batch separator natively when fed
        // through TSqlScript parsing.
        const string sql = """
            SELECT 1;
            GO
            SELECT 2;
            GO
            """;
        var batch = _parser.Parse(sql);

        batch.Errors.Should().BeEmpty();
        batch.Statements.Should().HaveCount(2);
        batch.Statements.Should().AllSatisfy(s => s.StatementKind.Should().Be(StatementKind.Select));
    }

    [Fact]
    public void Syntax_errors_populate_errors_collection()
    {
        var batch = _parser.Parse("SELECT FROM WHERE;");

        batch.HasErrors.Should().BeTrue();
        batch.Errors.Should().NotBeEmpty();
        batch.Errors.Should().AllSatisfy(e => e.Severity.Should().Be(ParseSeverity.Error));
    }

    [Fact]
    public void Provider_ast_round_trips_and_is_tagged()
    {
        var batch = _parser.Parse("SELECT 1;");

        batch.ProviderAst.Tag.Should().Be("tsql");
        batch.ProviderAst.Value.Should().NotBeNull();
    }

    [Fact]
    public void Predicate_injection_round_trip_select()
    {
        var batch = _parser.Parse("SELECT id FROM dbo.Users WHERE active = 1;");
        var directive = new PredicateInjectionDirective(
            new ObjectReference(ObjectKind.Table, null, null, "dbo", "Users"),
            "tenant_id = 42");

        var rewritten = _rewriter.Rewrite(batch, [directive]);

        rewritten.Should().Contain("tenant_id = 42");
        // Re-parse confirms it's still valid T-SQL.
        var reparsed = _parser.Parse(rewritten);
        reparsed.Errors.Should().BeEmpty();
    }

    [Fact]
    public void Predicate_injection_adds_where_when_missing()
    {
        var batch = _parser.Parse("SELECT id FROM dbo.Users;");
        var directive = new PredicateInjectionDirective(
            new ObjectReference(ObjectKind.Table, null, null, "dbo", "Users"),
            "tenant_id = 42");

        var rewritten = _rewriter.Rewrite(batch, [directive]);

        rewritten.Should().Contain("WHERE");
        rewritten.Should().Contain("tenant_id = 42");
        _parser.Parse(rewritten).Errors.Should().BeEmpty();
    }

    [Fact]
    public void Predicate_injection_targets_update_statement()
    {
        var batch = _parser.Parse("UPDATE dbo.Users SET name = 'x' WHERE id = 1;");
        var directive = new PredicateInjectionDirective(
            new ObjectReference(ObjectKind.Table, null, null, "dbo", "Users"),
            "tenant_id = 42");

        var rewritten = _rewriter.Rewrite(batch, [directive]);

        rewritten.Should().Contain("tenant_id = 42");
        var reparsed = _parser.Parse(rewritten);
        reparsed.Errors.Should().BeEmpty();
        reparsed.Statements[0].StatementKind.Should().Be(StatementKind.Update);
    }

    [Fact]
    public void Rewriter_passthrough_when_no_directives()
    {
        var batch = _parser.Parse("SELECT 1;");
        _rewriter.Rewrite(batch, []).Should().Be("SELECT 1;");
    }
}
