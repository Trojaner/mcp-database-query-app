using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryExecution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// End-to-end checks for the AST-driven pipeline once wired through the
/// in-process harness DI container. Asserts both DI wiring (the right step
/// implementations resolve) and behaviour (read-only/destructive rejections
/// fire before the SQL would ever reach a provider).
/// </summary>
public sealed class PipelineEnforcementTests
{
    private sealed class CountingConnection : IDatabaseConnection
    {
        public CountingConnection(string id, DatabaseKind kind, bool isReadOnly)
        {
            Id = id;
            Kind = kind;
            IsReadOnly = isReadOnly;
            Descriptor = new ConnectionDescriptor
            {
                Id = id,
                Name = id,
                Provider = kind,
                Host = "fake",
                Database = "fake",
                Username = "fake",
                SslMode = "Disable",
                ReadOnly = isReadOnly,
            };
        }

        public string Id { get; }
        public DatabaseKind Kind { get; }
        public ConnectionDescriptor Descriptor { get; }
        public bool IsReadOnly { get; }
        public int QueryCalls { get; private set; }
        public int NonQueryCalls { get; private set; }

        public Task<QueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Task.FromResult(new QueryResult([], [], 0, false, 0, 0));
        }

        public Task<long> ExecuteNonQueryAsync(NonQueryRequest request, CancellationToken cancellationToken)
        {
            NonQueryCalls++;
            return Task.FromResult(0L);
        }

        public Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(IReadOnlyList<TableInfo> Items, long Total)> ListTablesAsync(string? schema, PageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConfirmer : IDestructiveOperationConfirmer
    {
        private readonly bool? _verdict;
        public int CallCount { get; private set; }

        public FakeConfirmer(bool? verdict) { _verdict = verdict; }

        public Task<bool?> ConfirmAsync(IReadOnlyList<DestructiveStatement> statements, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_verdict);
        }
    }

    [Fact]
    public async Task ReadOnly_connection_rejects_INSERT_before_provider_executes()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();
        var conn = new CountingConnection("ro1", DatabaseKind.Postgres, isReadOnly: true);
        var ctx = new QueryExecutionContext(
            "INSERT INTO t (id) VALUES (1)",
            null,
            conn,
            QueryExecutionMode.Write,
            confirmDestructive: true,
            confirmUnlimited: false);

        var act = () => pipeline.ExecuteAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<ReadOnlyConnectionViolationException>();
        conn.NonQueryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Read_path_rejects_DROP_TABLE()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();
        var conn = new CountingConnection("rw1", DatabaseKind.Postgres, isReadOnly: false);
        var ctx = new QueryExecutionContext(
            "DROP TABLE users",
            null,
            conn,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);

        var act = () => pipeline.ExecuteAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<MutationOnReadPathException>();
        conn.QueryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Destructive_DELETE_without_confirm_invokes_confirmer()
    {
        await using var harness = await InProcessServerHarness.StartAsync(
            configOverrides: new Dictionary<string, string?>
            {
                // Disable the bypass so the confirmer is reached even with
                // confirm=true on the request.
                ["McpDatabaseQueryApp:DangerouslySkipPermissions"] = "false",
            });

        // Replace the MCP confirmer with a counting one so we don't depend on
        // the elicitation transport for this assertion.
        var fake = new FakeConfirmer(true);
        var services = harness.Services;
        // We can't re-register, but the resolved pipeline holds onto the
        // original — so build a one-off pipeline using the same parsers.
        var pipeline = new QueryPipeline(new IQueryExecutionStep[]
        {
            new McpDatabaseQueryApp.Core.QueryExecution.Steps.ParseQueryStep(services.GetRequiredService<McpDatabaseQueryApp.Core.QueryParsing.IQueryParserFactory>()),
            new McpDatabaseQueryApp.Core.QueryExecution.Steps.ReadOnlyEnforcementStep(),
            new McpDatabaseQueryApp.Core.QueryExecution.Steps.DestructiveConfirmationStep(fake, services.GetRequiredService<McpDatabaseQueryApp.Core.Configuration.McpDatabaseQueryAppOptions>()),
        });

        var conn = new CountingConnection("rw2", DatabaseKind.Postgres, isReadOnly: false);
        var ctx = new QueryExecutionContext(
            "DELETE FROM users",
            null,
            conn,
            QueryExecutionMode.Write,
            confirmDestructive: false,
            confirmUnlimited: false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);
        fake.CallCount.Should().Be(1);
    }

    [Fact]
    public async Task Destructive_DELETE_with_skip_permissions_and_confirm_skips_confirmer()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var fake = new FakeConfirmer(false);
        var services = harness.Services;
        var pipeline = new QueryPipeline(new IQueryExecutionStep[]
        {
            new McpDatabaseQueryApp.Core.QueryExecution.Steps.ParseQueryStep(services.GetRequiredService<McpDatabaseQueryApp.Core.QueryParsing.IQueryParserFactory>()),
            new McpDatabaseQueryApp.Core.QueryExecution.Steps.ReadOnlyEnforcementStep(),
            new McpDatabaseQueryApp.Core.QueryExecution.Steps.DestructiveConfirmationStep(fake, services.GetRequiredService<McpDatabaseQueryApp.Core.Configuration.McpDatabaseQueryAppOptions>()),
        });

        var conn = new CountingConnection("rw3", DatabaseKind.Postgres, isReadOnly: false);
        var ctx = new QueryExecutionContext(
            "DELETE FROM users",
            null,
            conn,
            QueryExecutionMode.Write,
            confirmDestructive: true,
            confirmUnlimited: false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);
        fake.CallCount.Should().Be(0);
    }
}
