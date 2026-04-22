using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Core.Storage;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Storage;

public sealed class SqliteMetadataStoreTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly SqliteMetadataStore _store;

    public SqliteMetadataStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-database-query-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var options = new McpDatabaseQueryAppOptions { MetadataDbPath = Path.Combine(_tempDir, "meta.db") };
        _store = new SqliteMetadataStore(options);
    }

    public async Task InitializeAsync()
    {
        await _store.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // best effort
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Initialize_is_idempotent()
    {
        await _store.InitializeAsync(CancellationToken.None);
        await _store.InitializeAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Upsert_and_Get_database_roundtrips()
    {
        var descriptor = SampleDescriptor("analytics");
        await _store.UpsertDatabaseAsync(descriptor, Cipher(), Nonce(), CancellationToken.None);

        var record = await _store.GetDatabaseAsync("analytics", CancellationToken.None);

        record.Should().NotBeNull();
        record!.Descriptor.Name.Should().Be("analytics");
        record.Descriptor.Host.Should().Be(descriptor.Host);
    }

    [Fact]
    public async Task Upsert_replaces_on_name_conflict()
    {
        var original = SampleDescriptor("analytics") with { Host = "old.example.com" };
        var updated = SampleDescriptor("analytics") with { Host = "new.example.com" };

        await _store.UpsertDatabaseAsync(original, Cipher(), Nonce(), CancellationToken.None);
        await _store.UpsertDatabaseAsync(updated, Cipher(), Nonce(), CancellationToken.None);

        var record = await _store.GetDatabaseAsync("analytics", CancellationToken.None);
        record!.Descriptor.Host.Should().Be("new.example.com");
    }

    [Fact]
    public async Task ListDatabases_paginates_and_counts()
    {
        for (var i = 0; i < 25; i++)
        {
            await _store.UpsertDatabaseAsync(SampleDescriptor($"db{i:D2}"), Cipher(), Nonce(), CancellationToken.None);
        }

        var (items, total) = await _store.ListDatabasesAsync(0, 10, null, CancellationToken.None);
        items.Should().HaveCount(10);
        total.Should().Be(25);

        var (second, _) = await _store.ListDatabasesAsync(10, 10, null, CancellationToken.None);
        second.Should().HaveCount(10);
        second.First().Name.Should().NotBe(items.First().Name);
    }

    [Fact]
    public async Task ListDatabases_filter_matches_name_or_database()
    {
        await _store.UpsertDatabaseAsync(SampleDescriptor("orders") with { Database = "prod" }, Cipher(), Nonce(), CancellationToken.None);
        await _store.UpsertDatabaseAsync(SampleDescriptor("reports") with { Database = "prod_reporting" }, Cipher(), Nonce(), CancellationToken.None);
        await _store.UpsertDatabaseAsync(SampleDescriptor("analytics") with { Database = "staging" }, Cipher(), Nonce(), CancellationToken.None);

        var (items, total) = await _store.ListDatabasesAsync(0, 100, "prod", CancellationToken.None);

        total.Should().Be(2);
        items.Select(i => i.Name).Should().BeEquivalentTo(["orders", "reports"]);
    }

    [Fact]
    public async Task Delete_removes_database()
    {
        await _store.UpsertDatabaseAsync(SampleDescriptor("x"), Cipher(), Nonce(), CancellationToken.None);

        var removed = await _store.DeleteDatabaseAsync("x", CancellationToken.None);

        removed.Should().BeTrue();
        (await _store.GetDatabaseAsync("x", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_returns_false_when_missing()
    {
        var removed = await _store.DeleteDatabaseAsync("not-there", CancellationToken.None);
        removed.Should().BeFalse();
    }

    [Fact]
    public async Task UpdateMetadata_throws_when_missing()
    {
        Func<Task> act = async () => await _store.UpdateDatabaseMetadataAsync(SampleDescriptor("ghost"), CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Script_crud_roundtrips()
    {
        var record = new ScriptRecord
        {
            Id = ConnectionIdFactory.NewScriptId(),
            Name = "clean-audit",
            SqlText = "DELETE FROM audit;",
            Destructive = true,
            Tags = new[] { "maintenance" },
        };
        await _store.UpsertScriptAsync(record, CancellationToken.None);

        var fetched = await _store.GetScriptAsync("clean-audit", CancellationToken.None);
        fetched.Should().NotBeNull();
        fetched!.Destructive.Should().BeTrue();
        fetched.Tags.Should().ContainSingle().Which.Should().Be("maintenance");

        var (items, total) = await _store.ListScriptsAsync(0, 10, null, CancellationToken.None);
        total.Should().Be(1);
        items.Should().ContainSingle();

        (await _store.DeleteScriptAsync("clean-audit", CancellationToken.None)).Should().BeTrue();
        (await _store.GetScriptAsync("clean-audit", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ResultSet_insert_and_expire()
    {
        var rowsPath = Path.Combine(_tempDir, "rows.jsonl");
        await File.WriteAllTextAsync(rowsPath, "[1]\n");
        var record = new ResultSetRecord
        {
            Id = ConnectionIdFactory.NewResultSetId(),
            ConnectionId = "conn_test",
            Columns = new[] { new QueryColumn("a", "int", 0) },
            RowsPath = rowsPath,
            TotalRows = 1,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
        };
        await _store.InsertResultSetAsync(record, CancellationToken.None);

        (await _store.GetResultSetAsync(record.Id, CancellationToken.None)).Should().NotBeNull();

        await _store.PurgeExpiredResultSetsAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        (await _store.GetResultSetAsync(record.Id, CancellationToken.None)).Should().BeNull();
        File.Exists(rowsPath).Should().BeFalse();
    }

    private static ConnectionDescriptor SampleDescriptor(string name) => new()
    {
        Id = ConnectionIdFactory.NewDatabaseId(),
        Name = name,
        Provider = DatabaseKind.Postgres,
        Host = "localhost",
        Port = 5432,
        Database = "app",
        Username = "reader",
        SslMode = "Require",
        TrustServerCertificate = false,
        ReadOnly = true,
        DefaultSchema = "public",
        Tags = Array.Empty<string>(),
    };

    private static byte[] Cipher() => new byte[32];

    private static byte[] Nonce() => new byte[12];
}
