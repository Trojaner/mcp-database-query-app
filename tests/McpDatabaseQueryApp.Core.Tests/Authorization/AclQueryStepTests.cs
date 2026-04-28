using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryExecution.Steps;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Core.Tests.QueryExecution;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Authorization;

public sealed class AclQueryStepTests
{
    private static readonly ProfileId Tenant = new("p_test");

    [Fact]
    public async Task Allowed_table_calls_next()
    {
        var evaluator = new FakeEvaluator((req, _) => Allow(req));
        var step = new AclQueryStep(evaluator);

        var ctx = MakeContext(BatchSelect("public", "users", "id"));
        var nextCalled = false;
        await step.ExecuteAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);

        nextCalled.Should().BeTrue();
        evaluator.Calls.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Denied_table_throws_AccessDenied()
    {
        var evaluator = new FakeEvaluator((req, _) => Task.FromResult(
            new AclDecision(AclEffect.Deny, MatchingEntry: null, Reason: "test deny")));
        var step = new AclQueryStep(evaluator);

        var ctx = MakeContext(BatchSelect("public", "users"));
        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<AccessDeniedException>();
        ex.Which.Operation.Should().Be(AclOperation.Read);
        ex.Which.Scope.Should().Contain("users");
        ex.Which.Reason.Should().Be("test deny");
    }

    [Fact]
    public async Task Insert_on_table_with_only_Read_allowed_is_denied()
    {
        var evaluator = new FakeEvaluator((req, _) => req.Operation == AclOperation.Read
            ? Allow(req)
            : Task.FromResult(new AclDecision(AclEffect.Deny, null, "no insert")));
        var step = new AclQueryStep(evaluator);

        var batch = BatchAction(StatementKind.Insert, ActionKind.Insert, "public", "users",
            new ColumnReference("public", "users", "name", ColumnUsage.Inserted));
        var ctx = MakeContext(batch);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AccessDeniedException>();
        ex.Which.Operation.Should().Be(AclOperation.Insert);
    }

    [Fact]
    public async Task ColumnLevel_deny_names_offending_column()
    {
        var evaluator = new FakeEvaluator((req, _) =>
        {
            if (req.Column == "ssn")
            {
                return Task.FromResult(new AclDecision(AclEffect.Deny, null, "column-level deny on ssn"));
            }

            return Allow(req);
        });
        var step = new AclQueryStep(evaluator);

        var batch = BatchAction(StatementKind.Select, ActionKind.Read, "public", "users",
            new ColumnReference("public", "users", "id", ColumnUsage.Projected),
            new ColumnReference("public", "users", "ssn", ColumnUsage.Projected));
        var ctx = MakeContext(batch);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AccessDeniedException>();
        ex.Which.Scope.Should().Contain("ssn");
    }

    [Fact]
    public async Task MultiStatement_batch_denies_when_second_statement_denies()
    {
        var evaluator = new FakeEvaluator((req, _) =>
        {
            if (string.Equals(req.Object.Name, "secret", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AclDecision(AclEffect.Deny, null, "secret table"));
            }

            return Allow(req);
        });
        var step = new AclQueryStep(evaluator);

        var stmt1 = StatementWith(StatementKind.Select, ActionKind.Read, "public", "users");
        var stmt2 = StatementWith(StatementKind.Select, ActionKind.Read, "public", "secret");
        var batch = TestFakes.Batch(DatabaseKind.Postgres, "SELECT … ; SELECT …", stmt1, stmt2);
        var ctx = MakeContext(batch);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        var ex = await act.Should().ThrowAsync<AccessDeniedException>();
        ex.Which.Scope.Should().Contain("secret");
    }

    [Fact]
    public async Task Throws_when_parse_step_did_not_run()
    {
        var step = new AclQueryStep(new FakeEvaluator((_, _) => throw new InvalidOperationException()));
        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, false);
        var ctx = new QueryExecutionContext("SELECT 1", null, conn, QueryExecutionMode.Read, false, false);

        var act = () => step.ExecuteAsync(ctx, _ => Task.CompletedTask, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SelectStar_synthetic_column_skips_column_check()
    {
        var evaluator = new FakeEvaluator((req, _) =>
        {
            req.Column.Should().BeNull(because: "table-level read should run, '*' should not produce a column-level eval");
            return Allow(req);
        });
        var step = new AclQueryStep(evaluator);

        var batch = BatchAction(StatementKind.Select, ActionKind.Read, "public", "users",
            new ColumnReference(null, null, "*", ColumnUsage.Projected));
        var ctx = MakeContext(batch);

        var nextCalled = false;
        await step.ExecuteAsync(ctx, _ => { nextCalled = true; return Task.CompletedTask; }, CancellationToken.None);
        nextCalled.Should().BeTrue();
    }

    private static QueryExecutionContext MakeContext(ParsedBatch parsed)
    {
        var conn = new TestFakes.FakeConnection("c1", DatabaseKind.Postgres, false);
        var ctx = new QueryExecutionContext(parsed.OriginalSql, null, conn, QueryExecutionMode.Read, false, false)
        {
            Parsed = parsed,
            Profile = new Profile(
                Tenant,
                Name: Tenant.Value,
                Subject: Tenant.Value,
                Issuer: null,
                CreatedAt: DateTimeOffset.UtcNow,
                Status: ProfileStatus.Active,
                Metadata: new Dictionary<string, string>(StringComparer.Ordinal)),
        };
        return ctx;
    }

    private static ParsedBatch BatchSelect(string schema, string table, params string[] columns)
    {
        var cols = columns.Select(c => new ColumnReference(schema, table, c, ColumnUsage.Projected)).ToArray();
        return BatchAction(StatementKind.Select, ActionKind.Read, schema, table, cols);
    }

    private static ParsedBatch BatchAction(
        StatementKind kind,
        ActionKind action,
        string schema,
        string table,
        params ColumnReference[] columns)
    {
        var stmt = StatementWith(kind, action, schema, table, columns);
        return TestFakes.Batch(DatabaseKind.Postgres, $"-- {kind} on {schema}.{table}", stmt);
    }

    private static ParsedStatement StatementWith(
        StatementKind kind,
        ActionKind action,
        string schema,
        string table,
        params ColumnReference[] columns)
    {
        var target = new ObjectReference(ObjectKind.Table, null, null, schema, table);
        var qaction = new ParsedQueryAction(action, target, columns);
        return new ParsedStatement(
            kind,
            isMutation: action != ActionKind.Read,
            isDestructive: false,
            originalText: $"{action} {schema}.{table}",
            range: new SourceRange(0, 1),
            actions: new[] { qaction },
            warnings: Array.Empty<ParseDiagnostic>(),
            providerAst: ProviderAst.None);
    }

    private static Task<AclDecision> Allow(IAclEvaluationRequest req)
        => Task.FromResult(new AclDecision(AclEffect.Allow, null, $"allow {req.Operation}"));

    private sealed class FakeEvaluator : IAclEvaluator
    {
        private readonly Func<IAclEvaluationRequest, CancellationToken, Task<AclDecision>> _impl;
        public int Calls { get; private set; }

        public FakeEvaluator(Func<IAclEvaluationRequest, CancellationToken, Task<AclDecision>> impl)
        {
            _impl = impl;
        }

        public Task<AclDecision> EvaluateAsync(IAclEvaluationRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return _impl(request, cancellationToken);
        }
    }
}
