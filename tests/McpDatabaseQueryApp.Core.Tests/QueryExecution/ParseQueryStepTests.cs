using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryExecution.Steps;
using McpDatabaseQueryApp.Core.QueryParsing;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.QueryExecution;

public sealed class ParseQueryStepTests
{
    [Fact]
    public async Task Populates_parsed_batch_on_success()
    {
        var batch = TestFakes.Batch(
            DatabaseKind.Postgres,
            "SELECT 1",
            TestFakes.Statement(StatementKind.Select, "SELECT 1", isMutation: false, isDestructive: false));

        var factory = new TestFakes.FakeParserFactory()
            .Add(new TestFakes.FakeParser(DatabaseKind.Postgres, _ => batch));
        var step = new ParseQueryStep(factory);

        var ctx = new QueryExecutionContext(
            "SELECT 1",
            parameters: null,
            new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, isReadOnly: false),
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);

        var nextCalled = false;
        await step.ExecuteAsync(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        ctx.Parsed.Should().BeSameAs(batch);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Throws_QuerySyntaxException_on_parse_failure_and_short_circuits()
    {
        var factory = new TestFakes.FakeParserFactory()
            .Add(new TestFakes.FakeParser(DatabaseKind.Postgres, _ => throw new QueryParseException("bad token at line 1")));
        var step = new ParseQueryStep(factory);

        var ctx = new QueryExecutionContext(
            "BAD SQL",
            parameters: null,
            new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, isReadOnly: false),
            QueryExecutionMode.Write,
            confirmDestructive: false,
            confirmUnlimited: false);

        var nextCalled = false;
        var act = () => step.ExecuteAsync(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<QuerySyntaxException>();
        ex.Which.Dialect.Should().Be(DatabaseKind.Postgres);
        ex.Which.Message.Should().Contain("Postgres");
        ex.Which.Message.Should().Contain("bad token");
        nextCalled.Should().BeFalse();
    }
}
