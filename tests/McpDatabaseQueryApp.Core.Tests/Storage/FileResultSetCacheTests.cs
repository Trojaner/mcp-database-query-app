using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Core.Storage;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Storage;

public sealed class FileResultSetCacheTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly SqliteMetadataStore _store;
    private readonly FileResultSetCache _cache;

    public FileResultSetCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-database-query-app-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        var options = new McpDatabaseQueryAppOptions
        {
            MetadataDbPath = Path.Combine(_tempDir, "meta.db"),
            ResultSetTtl = TimeSpan.FromMinutes(10),
        };
        _store = new SqliteMetadataStore(options, new ProfileContextAccessor());
        _cache = new FileResultSetCache(options, _store);
    }

    public Task InitializeAsync() => _store.InitializeAsync(CancellationToken.None);

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
    public async Task Store_and_page_through_rows()
    {
        var columns = new List<QueryColumn>
        {
            new("id", "int", 0),
            new("name", "text", 1),
        };
        var rows = Enumerable.Range(0, 25)
            .Select(i => (IReadOnlyList<object?>)new object?[] { i, $"name-{i}" })
            .ToList();
        var query = new QueryResult(columns, rows, rows.Count, Truncated: true, TotalRowsAvailable: rows.Count, ExecutionMs: 10);

        var id = await _cache.StoreAsync("conn_test", query, CancellationToken.None);

        var page1 = await _cache.GetPageAsync(id, offset: 0, limit: 10, CancellationToken.None);
        page1.Should().NotBeNull();
        page1!.Rows.Should().HaveCount(10);
        page1.HasMore.Should().BeTrue();
        page1.TotalRows.Should().Be(25);

        var page2 = await _cache.GetPageAsync(id, offset: 10, limit: 10, CancellationToken.None);
        page2!.Rows.Should().HaveCount(10);
        page2.HasMore.Should().BeTrue();

        var page3 = await _cache.GetPageAsync(id, offset: 20, limit: 10, CancellationToken.None);
        page3!.Rows.Should().HaveCount(5);
        page3.HasMore.Should().BeFalse();
    }

    [Fact]
    public async Task Get_returns_null_when_missing()
    {
        var page = await _cache.GetPageAsync("result_nope", 0, 10, CancellationToken.None);
        page.Should().BeNull();
    }
}
