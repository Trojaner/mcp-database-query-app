using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Storage;

public sealed class SqliteIsolationRuleStoreTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly SqliteMetadataStore _metadata;
    private readonly SqliteProfileStore _profiles;
    private readonly StaticIsolationRuleRegistry _registry;
    private readonly SqliteIsolationRuleStore _store;

    public SqliteIsolationRuleStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-database-query-app-iso-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new McpDatabaseQueryAppOptions { MetadataDbPath = Path.Combine(_tempDir, "meta.db") };
        _metadata = new SqliteMetadataStore(_options, new ProfileContextAccessor());
        _profiles = new SqliteProfileStore(_options);
        _registry = new StaticIsolationRuleRegistry();
        _store = new SqliteIsolationRuleStore(_options, _registry);
    }

    public async Task InitializeAsync()
    {
        await _metadata.InitializeAsync(CancellationToken.None);
        await _profiles.EnsureDefaultAsync(CancellationToken.None);
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

    private static ConnectionDescriptor Conn(string host = "db.example.com", int port = 5432, string database = "analytics")
        => new()
        {
            Id = "c1",
            Name = "c1",
            Provider = DatabaseKind.Postgres,
            Host = host,
            Port = port,
            Database = database,
            Username = "test",
            SslMode = "Disable",
        };

    private static IsolationRule MakeDynamic(
        string id,
        string profile = "default",
        string host = "db.example.com",
        int port = 5432,
        string database = "analytics",
        string schema = "public",
        string table = "events",
        IsolationFilter? filter = null,
        int priority = 0)
    {
        return new IsolationRule(
            new IsolationRuleId(id),
            new ProfileId(profile),
            new IsolationScope(host, port, database, schema, table),
            filter ?? new IsolationFilter.EqualityFilter("tenant_id", 42),
            IsolationRuleSource.Dynamic,
            priority,
            Description: null);
    }

    [Fact]
    public async Task Round_trip_dynamic_rule_with_each_filter_kind()
    {
        await _store.UpsertAsync(MakeDynamic("eq", filter: new IsolationFilter.EqualityFilter("tenant_id", 42)), CancellationToken.None);
        await _store.UpsertAsync(MakeDynamic("in", schema: "public", table: "regions", filter: new IsolationFilter.InListFilter("region", new object?[] { "us", "eu" })), CancellationToken.None);
        await _store.UpsertAsync(MakeDynamic("raw", schema: "public", table: "audits", filter: new IsolationFilter.RawSqlFilter("deleted_at IS NULL", new Dictionary<string, object?>())), CancellationToken.None);

        var rules = await _store.ListAsync(ProfileId.Default, Conn(), CancellationToken.None);

        rules.Should().HaveCount(3);
        rules.Should().Contain(r => r.Id.Value == "eq" && r.Filter is IsolationFilter.EqualityFilter);
        rules.Should().Contain(r => r.Id.Value == "in" && r.Filter is IsolationFilter.InListFilter);
        rules.Should().Contain(r => r.Id.Value == "raw" && r.Filter is IsolationFilter.RawSqlFilter);

        var inListRule = rules.Single(r => r.Id.Value == "in");
        var inFilter = (IsolationFilter.InListFilter)inListRule.Filter;
        inFilter.Values.Should().BeEquivalentTo(new object?[] { "us", "eu" });
    }

    [Fact]
    public async Task List_filters_by_connection_scope()
    {
        await _store.UpsertAsync(MakeDynamic("a", host: "db-a.example.com"), CancellationToken.None);
        await _store.UpsertAsync(MakeDynamic("b", host: "db-b.example.com"), CancellationToken.None);

        var hits = await _store.ListAsync(ProfileId.Default, Conn(host: "db-a.example.com"), CancellationToken.None);

        hits.Should().ContainSingle().Which.Id.Value.Should().Be("a");
    }

    [Fact]
    public async Task Cross_profile_isolation_is_preserved()
    {
        await _store.UpsertAsync(MakeDynamic("d", profile: "default"), CancellationToken.None);
        await _profiles.UpsertAsync(new Profile(
            new ProfileId("tenantX"),
            Name: "tenantX",
            Subject: "tenantX",
            Issuer: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Status: ProfileStatus.Active,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)), CancellationToken.None);
        await _store.UpsertAsync(MakeDynamic("x", profile: "tenantX"), CancellationToken.None);

        var defaultHits = await _store.ListAsync(ProfileId.Default, Conn(), CancellationToken.None);
        var tenantHits = await _store.ListAsync(new ProfileId("tenantX"), Conn(), CancellationToken.None);

        defaultHits.Select(r => r.Id.Value).Should().BeEquivalentTo(new[] { "d" });
        tenantHits.Select(r => r.Id.Value).Should().BeEquivalentTo(new[] { "x" });
    }

    [Fact]
    public async Task Static_rules_are_read_through_and_sort_with_dynamic_rules()
    {
        var staticRule = new IsolationRule(
            new IsolationRuleId("s-1"),
            ProfileId.Default,
            new IsolationScope("db.example.com", 5432, "analytics", "public", "events"),
            new IsolationFilter.EqualityFilter("region", "us"),
            IsolationRuleSource.Static,
            Priority: 1000,
            Description: null);
        _registry.Replace(new[] { staticRule });

        await _store.UpsertAsync(MakeDynamic("d-1", priority: 500), CancellationToken.None);

        var hits = await _store.ListAsync(ProfileId.Default, Conn(), CancellationToken.None);

        hits.Should().HaveCount(2);
        hits[0].Id.Value.Should().Be("s-1");
        hits[1].Id.Value.Should().Be("d-1");
    }

    [Fact]
    public async Task Upsert_on_static_rule_throws()
    {
        var staticRule = new IsolationRule(
            new IsolationRuleId("static-only"),
            ProfileId.Default,
            new IsolationScope("h", 1, "d", "s", "t"),
            new IsolationFilter.EqualityFilter("c", 1),
            IsolationRuleSource.Static,
            Priority: 0,
            Description: null);
        _registry.Replace(new[] { staticRule });

        Func<Task> act = () => _store.UpsertAsync(staticRule, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_on_static_rule_throws()
    {
        var staticRule = new IsolationRule(
            new IsolationRuleId("immutable"),
            ProfileId.Default,
            new IsolationScope("h", 1, "d", "s", "t"),
            new IsolationFilter.EqualityFilter("c", 1),
            IsolationRuleSource.Static,
            Priority: 0,
            Description: null);
        _registry.Replace(new[] { staticRule });

        Func<Task> act = () => _store.DeleteAsync(new IsolationRuleId("immutable"), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Delete_returns_false_for_missing_id_and_true_after_upsert()
    {
        (await _store.DeleteAsync(new IsolationRuleId("nope"), CancellationToken.None)).Should().BeFalse();

        await _store.UpsertAsync(MakeDynamic("present"), CancellationToken.None);
        (await _store.DeleteAsync(new IsolationRuleId("present"), CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task Get_returns_static_rule_before_querying_sqlite()
    {
        var staticRule = new IsolationRule(
            new IsolationRuleId("look-me-up"),
            ProfileId.Default,
            new IsolationScope("h", 1, "d", "s", "t"),
            new IsolationFilter.EqualityFilter("c", 1),
            IsolationRuleSource.Static,
            Priority: 0,
            Description: null);
        _registry.Replace(new[] { staticRule });

        var fetched = await _store.GetAsync(new IsolationRuleId("look-me-up"), CancellationToken.None);

        fetched.Should().NotBeNull();
        fetched!.Source.Should().Be(IsolationRuleSource.Static);
    }
}
