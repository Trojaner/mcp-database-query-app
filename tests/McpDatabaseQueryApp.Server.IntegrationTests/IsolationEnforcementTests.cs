using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryExecution;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// End-to-end checks for the data-isolation pipeline step. Uses a
/// capturing fake connection rather than a live database so that the SQL
/// the server "would have executed" is observable from the test.
/// </summary>
public sealed class IsolationEnforcementTests
{
    private sealed class CapturingConnection : IDatabaseConnection
    {
        public CapturingConnection(string id, string host, int port, string database)
        {
            Id = id;
            Kind = DatabaseKind.Postgres;
            IsReadOnly = false;
            Descriptor = new ConnectionDescriptor
            {
                Id = id,
                Name = id,
                Provider = DatabaseKind.Postgres,
                Host = host,
                Port = port,
                Database = database,
                Username = "test",
                SslMode = "Disable",
            };
        }

        public string Id { get; }
        public DatabaseKind Kind { get; }
        public ConnectionDescriptor Descriptor { get; }
        public bool IsReadOnly { get; }

        public Task<QueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new QueryResult([], [], 0, false, 0, 0));

        public Task<long> ExecuteNonQueryAsync(NonQueryRequest request, CancellationToken cancellationToken)
            => Task.FromResult(0L);

        public Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(IReadOnlyList<TableInfo> Items, long Total)> ListTablesAsync(string? schema, PageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static IsolationRule DynamicEqualityRule(
        string id,
        ProfileId profileId,
        string host,
        int port,
        string database,
        string schema,
        string table,
        string column,
        object? value)
    {
        return new IsolationRule(
            new IsolationRuleId(id),
            profileId,
            new IsolationScope(host, port, database, schema, table),
            new IsolationFilter.EqualityFilter(column, value),
            IsolationRuleSource.Dynamic,
            Priority: 0,
            Description: null);
    }

    [Fact]
    public async Task Profile_with_rule_has_predicate_injected_into_executed_sql()
    {
        await using var harness = await InProcessServerHarness.StartAsync();
        var store = harness.Services.GetRequiredService<IIsolationRuleStore>();
        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();

        await store.UpsertAsync(DynamicEqualityRule(
            "rule-1",
            ProfileId.Default,
            host: "db.example.com",
            port: 5432,
            database: "analytics",
            schema: "public",
            table: "events",
            column: "tenant_id",
            value: 42), CancellationToken.None);

        var conn = new CapturingConnection("c-with-rule", "db.example.com", 5432, "analytics");
        var ctx = new QueryExecutionContext(
            "SELECT * FROM events",
            null,
            conn,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);

        ctx.Sql.Should().Contain("tenant_id");
        ctx.Items.Should().ContainKey(IsolationRuleStep.AppliedDirectivesItemKey);
        ctx.Parameters.Should().NotBeNull();
        // The store round-trips numeric values through JSON, which collapses int → long.
        ctx.Parameters!.Values.Should().Contain(v => Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture) == 42);
    }

    [Fact]
    public async Task Profile_without_rule_has_unmodified_sql()
    {
        await using var harness = await InProcessServerHarness.StartAsync();
        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();

        var conn = new CapturingConnection("c-no-rule", "db.example.com", 5432, "analytics");
        var ctx = new QueryExecutionContext(
            "SELECT * FROM events",
            null,
            conn,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);

        ctx.Sql.Should().Be("SELECT * FROM events");
        ctx.Items.Should().NotContainKey(IsolationRuleStep.AppliedDirectivesItemKey);
    }

    [Fact]
    public async Task Connection_to_unrelated_host_does_not_trigger_rule_within_same_profile()
    {
        await using var harness = await InProcessServerHarness.StartAsync();
        var store = harness.Services.GetRequiredService<IIsolationRuleStore>();
        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();

        await store.UpsertAsync(DynamicEqualityRule(
            "rule-host",
            ProfileId.Default,
            host: "db.example.com",
            port: 5432,
            database: "analytics",
            schema: "public",
            table: "events",
            column: "tenant_id",
            value: 1), CancellationToken.None);

        var differentHost = new CapturingConnection("c-other-host", "other-host", 5432, "analytics");
        var ctx = new QueryExecutionContext(
            "SELECT * FROM events",
            null,
            differentHost,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);

        ctx.Sql.Should().Be("SELECT * FROM events");
        ctx.Items.Should().NotContainKey(IsolationRuleStep.AppliedDirectivesItemKey);
    }

    [Fact]
    public async Task Rule_in_one_profile_does_not_apply_when_running_under_another_profile()
    {
        await using var harness = await InProcessServerHarness.StartAsync();
        var store = harness.Services.GetRequiredService<IIsolationRuleStore>();
        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();

        var profileA = new ProfileId("profileA");
        var profileB = new ProfileId("profileB");
        await harness.UseProfileAsync(profileA);
        await store.UpsertAsync(DynamicEqualityRule(
            "rule-A",
            profileA,
            host: "db.example.com",
            port: 5432,
            database: "analytics",
            schema: "public",
            table: "events",
            column: "tenant_id",
            value: 1), CancellationToken.None);

        await harness.UseProfileAsync(profileB);

        var conn = new CapturingConnection("c-B", "db.example.com", 5432, "analytics");
        var ctx = new QueryExecutionContext(
            "SELECT * FROM events",
            null,
            conn,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);

        ctx.Sql.Should().Be("SELECT * FROM events");
        ctx.Items.Should().NotContainKey(IsolationRuleStep.AppliedDirectivesItemKey);
    }

    [Fact]
    public async Task Static_rule_from_appsettings_is_enforced_after_harness_start()
    {
        await using var harness = await InProcessServerHarness.StartAsync(
            configOverrides: new Dictionary<string, string?>
            {
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:ProfileId"] = "default",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Host"] = "db.example.com",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Port"] = "5432",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:DatabaseName"] = "analytics",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Schema"] = "public",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Table"] = "events",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Filter:Kind"] = "Equality",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Filter:Column"] = "tenant_id",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Filter:Value"] = "42",
                ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Priority"] = "1000",
            });

        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();
        var conn = new CapturingConnection("c-static", "db.example.com", 5432, "analytics");
        var ctx = new QueryExecutionContext(
            "SELECT * FROM events",
            null,
            conn,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);

        ctx.Sql.Should().Contain("tenant_id");
        ctx.Items.Should().ContainKey(IsolationRuleStep.AppliedDirectivesItemKey);
    }
}
