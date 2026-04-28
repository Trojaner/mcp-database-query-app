using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryParsing;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.QueryExecution;

public sealed class QueryClassifierTests
{
    /// <summary>
    /// Builds a classifier whose parser returns the supplied statements
    /// verbatim. Keeps the matrix here driven by the AST contract rather
    /// than any concrete parser implementation.
    /// </summary>
    private static IQueryClassifier MakeClassifier(params ParsedStatement[] statements)
    {
        var sql = statements.Length == 0 ? string.Empty : string.Join(";", statements.Select(static s => s.OriginalText));
        var batch = TestFakes.Batch(DatabaseKind.Postgres, sql, statements);
        var factory = new TestFakes.FakeParserFactory()
            .Add(new TestFakes.FakeParser(DatabaseKind.Postgres, _ => batch));
        return new QueryClassifier(factory);
    }

    [Fact]
    public void SELECT_is_neither_mutation_nor_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Select, "SELECT 1", false, false));
        var result = c.Classify(DatabaseKind.Postgres, "SELECT 1");
        result.ContainsMutation.Should().BeFalse();
        result.ContainsDestructive.Should().BeFalse();
        result.DestructiveStatements.Should().BeEmpty();
    }

    [Fact]
    public void INSERT_is_mutation_but_not_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Insert, "INSERT INTO t VALUES (1)", true, false));
        var result = c.Classify(DatabaseKind.Postgres, "INSERT INTO t VALUES (1)");
        result.ContainsMutation.Should().BeTrue();
        result.ContainsDestructive.Should().BeFalse();
    }

    [Fact]
    public void UPDATE_with_WHERE_is_mutation_but_not_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Update, "UPDATE t SET v=1 WHERE id=1", true, false));
        var result = c.Classify(DatabaseKind.Postgres, "UPDATE t SET v=1 WHERE id=1");
        result.ContainsMutation.Should().BeTrue();
        result.ContainsDestructive.Should().BeFalse();
    }

    [Fact]
    public void UPDATE_without_WHERE_is_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Update, "UPDATE t SET v=1", true, true));
        var result = c.Classify(DatabaseKind.Postgres, "UPDATE t SET v=1");
        result.ContainsMutation.Should().BeTrue();
        result.ContainsDestructive.Should().BeTrue();
        result.DestructiveStatements.Should().ContainSingle()
            .Which.Reason.Should().Contain("WHERE");
    }

    [Fact]
    public void DELETE_with_WHERE_is_mutation_but_not_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Delete, "DELETE FROM t WHERE id=1", true, false));
        var result = c.Classify(DatabaseKind.Postgres, "DELETE FROM t WHERE id=1");
        result.ContainsMutation.Should().BeTrue();
        result.ContainsDestructive.Should().BeFalse();
    }

    [Fact]
    public void DELETE_without_WHERE_is_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Delete, "DELETE FROM t", true, true));
        var result = c.Classify(DatabaseKind.Postgres, "DELETE FROM t");
        result.ContainsDestructive.Should().BeTrue();
    }

    [Fact]
    public void DROP_TABLE_is_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.DropTable, "DROP TABLE t", true, true));
        var result = c.Classify(DatabaseKind.Postgres, "DROP TABLE t");
        result.ContainsDestructive.Should().BeTrue();
        result.DestructiveStatements[0].Reason.Should().Contain("DROP TABLE");
    }

    [Fact]
    public void CREATE_TABLE_is_mutation_but_not_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.CreateTable, "CREATE TABLE t (id INT)", true, false));
        var result = c.Classify(DatabaseKind.Postgres, "CREATE TABLE t (id INT)");
        result.ContainsMutation.Should().BeTrue();
        result.ContainsDestructive.Should().BeFalse();
    }

    [Fact]
    public void TRUNCATE_is_destructive()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Truncate, "TRUNCATE t", true, true));
        var result = c.Classify(DatabaseKind.Postgres, "TRUNCATE t");
        result.ContainsDestructive.Should().BeTrue();
    }

    [Fact]
    public void MERGE_is_mutation()
    {
        var c = MakeClassifier(TestFakes.Statement(StatementKind.Merge, "MERGE INTO t USING s ON ...", true, false));
        var result = c.Classify(DatabaseKind.Postgres, "MERGE INTO t USING s ON ...");
        result.ContainsMutation.Should().BeTrue();
        result.ContainsDestructive.Should().BeFalse();
    }

    [Fact]
    public void Whitespace_returns_empty_classification()
    {
        var c = MakeClassifier();
        var result = c.Classify(DatabaseKind.Postgres, "   \n   ");
        result.Should().BeSameAs(QueryClassification.Empty);
    }

    [Fact]
    public void Parser_failure_throws_QuerySyntaxException()
    {
        var factory = new TestFakes.FakeParserFactory()
            .Add(new TestFakes.FakeParser(DatabaseKind.Postgres, _ => throw new QueryParseException("syntax error")));
        var classifier = new QueryClassifier(factory);

        var act = () => classifier.Classify(DatabaseKind.Postgres, "BAD");
        act.Should().Throw<QuerySyntaxException>()
            .Which.Dialect.Should().Be(DatabaseKind.Postgres);
    }
}
