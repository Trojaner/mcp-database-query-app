using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryExecution.Steps;
using McpDatabaseQueryApp.Core.QueryParsing;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.QueryExecution;

public sealed class DestructiveConfirmationStepTests
{
    private sealed class FakeConfirmer : IDestructiveOperationConfirmer
    {
        private readonly bool? _verdict;
        public int CallCount { get; private set; }
        public IReadOnlyList<DestructiveStatement>? LastStatements { get; private set; }

        public FakeConfirmer(bool? verdict)
        {
            _verdict = verdict;
        }

        public Task<bool?> ConfirmAsync(IReadOnlyList<DestructiveStatement> statements, CancellationToken cancellationToken)
        {
            CallCount++;
            LastStatements = statements;
            return Task.FromResult(_verdict);
        }
    }

    private static QueryExecutionContext MakeContext(
        ParsedBatch parsed,
        bool confirmDestructive)
    {
        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, isReadOnly: false);
        return new QueryExecutionContext(parsed.OriginalSql, null, conn, QueryExecutionMode.Write, confirmDestructive, false)
        {
            Parsed = parsed,
        };
    }

    [Fact]
    public async Task DELETE_without_WHERE_asks_confirmer_with_kind_and_reason()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DELETE FROM users",
            TestFakes.Statement(StatementKind.Delete, "DELETE FROM users", isMutation: true, isDestructive: true));

        var confirmer = new FakeConfirmer(true);
        var step = new DestructiveConfirmationStep(confirmer, new McpDatabaseQueryAppOptions { DangerouslySkipPermissions = true });

        var nextCalled = false;
        await step.ExecuteAsync(MakeContext(parsed, false), _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        confirmer.CallCount.Should().Be(1);
        confirmer.LastStatements.Should().ContainSingle();
        confirmer.LastStatements![0].Kind.Should().Be(StatementKind.Delete);
        confirmer.LastStatements[0].Reason.Should().Contain("WHERE");
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task DELETE_with_WHERE_does_not_ask_confirmer()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DELETE FROM users WHERE id = 1",
            TestFakes.Statement(StatementKind.Delete, "DELETE FROM users WHERE id = 1", isMutation: true, isDestructive: false));

        var confirmer = new FakeConfirmer(false);
        var step = new DestructiveConfirmationStep(confirmer, new McpDatabaseQueryAppOptions());

        var nextCalled = false;
        await step.ExecuteAsync(MakeContext(parsed, false), _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        confirmer.CallCount.Should().Be(0);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmDestructive_true_with_skip_permissions_bypasses_confirmer()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DROP TABLE users",
            TestFakes.Statement(StatementKind.DropTable, "DROP TABLE users", isMutation: true, isDestructive: true));

        var confirmer = new FakeConfirmer(false);
        var step = new DestructiveConfirmationStep(confirmer, new McpDatabaseQueryAppOptions { DangerouslySkipPermissions = true });

        var nextCalled = false;
        await step.ExecuteAsync(MakeContext(parsed, confirmDestructive: true), _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        confirmer.CallCount.Should().Be(0);
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ConfirmDestructive_true_without_skip_permissions_still_asks_confirmer()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DROP TABLE users",
            TestFakes.Statement(StatementKind.DropTable, "DROP TABLE users", isMutation: true, isDestructive: true));

        var confirmer = new FakeConfirmer(true);
        var step = new DestructiveConfirmationStep(confirmer, new McpDatabaseQueryAppOptions { DangerouslySkipPermissions = false });

        await step.ExecuteAsync(MakeContext(parsed, confirmDestructive: true), _ => Task.CompletedTask, CancellationToken.None);

        confirmer.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Confirmer_returns_false_throws_cancelled()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DROP TABLE users",
            TestFakes.Statement(StatementKind.DropTable, "DROP TABLE users", isMutation: true, isDestructive: true));

        var confirmer = new FakeConfirmer(false);
        var step = new DestructiveConfirmationStep(confirmer, new McpDatabaseQueryAppOptions());

        var act = () => step.ExecuteAsync(MakeContext(parsed, false), _ => Task.CompletedTask, CancellationToken.None);
        await act.Should().ThrowAsync<DestructiveOperationCancelledException>();
    }

    [Fact]
    public async Task Confirmer_returns_null_throws_required()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DROP TABLE users",
            TestFakes.Statement(StatementKind.DropTable, "DROP TABLE users", isMutation: true, isDestructive: true));

        var confirmer = new FakeConfirmer(null);
        var step = new DestructiveConfirmationStep(confirmer, new McpDatabaseQueryAppOptions());

        var act = () => step.ExecuteAsync(MakeContext(parsed, false), _ => Task.CompletedTask, CancellationToken.None);
        await act.Should().ThrowAsync<DestructiveOperationConfirmationRequiredException>();
    }

    [Fact]
    public async Task Explain_mode_skips_confirmer()
    {
        var parsed = TestFakes.Batch(
            DatabaseKind.Postgres,
            "DROP TABLE users",
            TestFakes.Statement(StatementKind.DropTable, "DROP TABLE users", isMutation: true, isDestructive: true));

        var confirmer = new FakeConfirmer(false);
        var step = new DestructiveConfirmationStep(confirmer, new McpDatabaseQueryAppOptions());

        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, isReadOnly: false);
        var ctx = new QueryExecutionContext(parsed.OriginalSql, null, conn, QueryExecutionMode.Explain, false, false)
        {
            Parsed = parsed,
        };

        var nextCalled = false;
        await step.ExecuteAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        confirmer.CallCount.Should().Be(0);
        nextCalled.Should().BeTrue();
    }
}
