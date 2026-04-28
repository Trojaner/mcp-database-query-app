using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryExecution;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// End-to-end checks that the ACL pipeline step gates real query execution
/// through the in-process harness DI graph. Drives the pipeline directly
/// (rather than through <c>db_query</c>) so the test does not need a live
/// database — the goal is to assert that ACL enforcement happens before any
/// SQL would reach a provider.
/// </summary>
public sealed class AclEnforcementTests
{
    private sealed class CountingConnection : IDatabaseConnection
    {
        public CountingConnection(string id, DatabaseKind kind)
        {
            Id = id;
            Kind = kind;
            Descriptor = new ConnectionDescriptor
            {
                Id = id, Name = id, Provider = kind,
                Host = "test-host", Port = 5432, Database = "testdb",
                Username = "u", SslMode = "Disable",
            };
        }

        public string Id { get; }
        public DatabaseKind Kind { get; }
        public ConnectionDescriptor Descriptor { get; }
        public bool IsReadOnly => false;
        public int QueryCalls { get; private set; }

        public Task<QueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
        {
            QueryCalls++;
            return Task.FromResult(new QueryResult([], [], 0, false, 0, 0));
        }

        public Task<long> ExecuteNonQueryAsync(NonQueryRequest request, CancellationToken cancellationToken) => Task.FromResult(0L);
        public Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<(IReadOnlyList<TableInfo> Items, long Total)> ListTablesAsync(string? schema, PageRequest page, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task DefaultProfile_with_AllowAll_executes_any_select()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();
        var conn = new CountingConnection("c-default", DatabaseKind.Postgres);
        var ctx = new QueryExecutionContext("SELECT 1", null, conn, QueryExecutionMode.Read, false, false);

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);
        // Pipeline only authorizes; the test doesn't run the SQL.
    }

    [Fact]
    public async Task DefaultProfile_with_DenyAll_and_no_entries_blocks_select()
    {
        await using var harness = await InProcessServerHarness.StartAsync(
            configOverrides: new Dictionary<string, string?>
            {
                ["McpDatabaseQueryApp:Authorization:DefaultProfilePolicy"] = "DenyAll",
            });

        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();
        var conn = new CountingConnection("c-deny", DatabaseKind.Postgres);
        var ctx = new QueryExecutionContext("SELECT * FROM users", null, conn, QueryExecutionMode.Read, false, false);

        var act = () => pipeline.ExecuteAsync(ctx, CancellationToken.None);
        await act.Should().ThrowAsync<AccessDeniedException>();
        conn.QueryCalls.Should().Be(0);
    }

    [Fact]
    public async Task TwoProfiles_have_independent_ACLs()
    {
        await using var harness = await InProcessServerHarness.StartAsync(
            configOverrides: new Dictionary<string, string?>
            {
                ["McpDatabaseQueryApp:Authorization:DefaultProfilePolicy"] = "DenyAll",
            });

        var alice = new ProfileId("p_alice");
        var bob = new ProfileId("p_bob");

        // Provision both profiles up front.
        await harness.UseProfileAsync(alice);
        await harness.UseProfileAsync(bob);

        // Alice is allowed to read any table.
        var aclStore = harness.Services.GetRequiredService<IAclStore>();
        await aclStore.UpsertAsync(new AclEntry(
            AclEntryId.NewId(),
            alice,
            AclSubjectKind.Profile,
            AclObjectScope.Any,
            AclOperation.Read,
            AclEffect.Allow,
            Priority: 100,
            Description: "alice read"), CancellationToken.None);

        // Bob has no entries → default-deny.

        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();
        var conn = new CountingConnection("c-shared", DatabaseKind.Postgres);

        await harness.UseProfileAsync(alice);
        var aliceCtx = new QueryExecutionContext("SELECT * FROM users", null, conn, QueryExecutionMode.Read, false, false)
        {
            Profile = await harness.Services.GetRequiredService<IProfileStore>().GetAsync(alice, CancellationToken.None),
        };
        await pipeline.ExecuteAsync(aliceCtx, CancellationToken.None);

        await harness.UseProfileAsync(bob);
        var bobCtx = new QueryExecutionContext("SELECT * FROM users", null, conn, QueryExecutionMode.Read, false, false)
        {
            Profile = await harness.Services.GetRequiredService<IProfileStore>().GetAsync(bob, CancellationToken.None),
        };

        var act = () => pipeline.ExecuteAsync(bobCtx, CancellationToken.None);
        await act.Should().ThrowAsync<AccessDeniedException>();
    }

    [Fact]
    public async Task StaticEntry_from_appsettings_applies_to_evaluator()
    {
        await using var harness = await InProcessServerHarness.StartAsync(
            configOverrides: new Dictionary<string, string?>
            {
                ["McpDatabaseQueryApp:Authorization:DefaultProfilePolicy"] = "DenyAll",
                ["McpDatabaseQueryApp:Authorization:StaticEntries:0:ProfileId"] = "p_static",
                ["McpDatabaseQueryApp:Authorization:StaticEntries:0:Effect"] = "Allow",
                ["McpDatabaseQueryApp:Authorization:StaticEntries:0:AllowedOperations:0"] = "Read",
                ["McpDatabaseQueryApp:Authorization:StaticEntries:0:Description"] = "static read-everything for p_static",
            });

        var profile = new ProfileId("p_static");
        await harness.UseProfileAsync(profile);

        var pipeline = harness.Services.GetRequiredService<IQueryPipeline>();
        var conn = new CountingConnection("c-static", DatabaseKind.Postgres);
        var ctx = new QueryExecutionContext("SELECT * FROM anything", null, conn, QueryExecutionMode.Read, false, false)
        {
            Profile = await harness.Services.GetRequiredService<IProfileStore>().GetAsync(profile, CancellationToken.None),
        };

        await pipeline.ExecuteAsync(ctx, CancellationToken.None);
    }
}
