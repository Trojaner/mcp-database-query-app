using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryExecution.Steps;
using McpDatabaseQueryApp.Core.QueryParsing;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.QueryExecution;

public sealed class ReadOnlyEnforcementStepTests
{
    private static QueryExecutionContext MakeContext(
        ParsedBatch parsed,
        bool isReadOnly,
        QueryExecutionMode mode)
    {
        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, isReadOnly);
        var ctx = new QueryExecutionContext(parsed.OriginalSql, null, conn, mode, false, false)
        {
            Parsed = parsed,
        };
        return ctx;
    }

    [Fact]
    public async Task ReadOnly_connection_with_INSERT_throws()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "INSERT INTO x VALUES (1)",
            TestFakes.Statement(StatementKind.Insert, "INSERT INTO x VALUES (1)", isMutation: true, isDestructive: false));

        var step = new ReadOnlyEnforcementStep();
        var ctx = MakeContext(parsed, isReadOnly: true, QueryExecutionMode.Write);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<ReadOnlyConnectionViolationException>();
        ex.Which.OffendingStatements.Should().ContainSingle().Which.Should().Contain("Insert");
    }

    [Fact]
    public async Task ReadOnly_connection_with_SELECT_calls_next()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "SELECT 1",
            TestFakes.Statement(StatementKind.Select, "SELECT 1", isMutation: false, isDestructive: false));
        var step = new ReadOnlyEnforcementStep();
        var ctx = MakeContext(parsed, isReadOnly: true, QueryExecutionMode.Read);

        var nextCalled = false;
        await step.ExecuteAsync(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task Writable_connection_with_DROP_calls_next()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DROP TABLE x",
            TestFakes.Statement(StatementKind.DropTable, "DROP TABLE x", isMutation: true, isDestructive: true));
        var step = new ReadOnlyEnforcementStep();
        var ctx = MakeContext(parsed, isReadOnly: false, QueryExecutionMode.Write);

        var nextCalled = false;
        await step.ExecuteAsync(ctx, _ =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None);

        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ReadMode_with_UPDATE_throws()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "UPDATE t SET v = 1 WHERE id = 1",
            TestFakes.Statement(StatementKind.Update, "UPDATE t SET v = 1 WHERE id = 1", isMutation: true, isDestructive: false));
        var step = new ReadOnlyEnforcementStep();
        var ctx = MakeContext(parsed, isReadOnly: false, QueryExecutionMode.Read);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<MutationOnReadPathException>();
        ex.Which.OffendingStatements.Should().ContainSingle().Which.Should().Contain("Update");
    }

    [Fact]
    public async Task Throws_when_parse_step_did_not_run()
    {
        var step = new ReadOnlyEnforcementStep();
        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, false);
        var ctx = new QueryExecutionContext("SELECT 1", null, conn, QueryExecutionMode.Read, false, false);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
