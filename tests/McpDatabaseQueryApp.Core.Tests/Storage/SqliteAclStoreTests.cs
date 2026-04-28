using FluentAssertions;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Storage;

public sealed class SqliteAclStoreTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly SqliteMetadataStore _metadata;
    private readonly SqliteProfileStore _profiles;
    private readonly SqliteAclStore _store;

    public SqliteAclStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-database-query-app-acl-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new McpDatabaseQueryAppOptions { MetadataDbPath = Path.Combine(_tempDir, "meta.db") };
        _metadata = new SqliteMetadataStore(_options, new ProfileContextAccessor());
        _profiles = new SqliteProfileStore(_options);
        _store = new SqliteAclStore(_options);
    }

    public async Task InitializeAsync()
    {
        await _metadata.InitializeAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Default_profile_starts_with_no_entries()
    {
        var entries = await _store.ListAsync(ProfileId.Default, CancellationToken.None);
        entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Upsert_round_trips_an_entry()
    {
        await SeedProfileAsync(new ProfileId("p_a"));
        var entry = MakeEntry(new ProfileId("p_a"), priority: 42, table: "users", description: "alice rule");

        await _store.UpsertAsync(entry, CancellationToken.None);

        var got = await _store.GetAsync(entry.Id, CancellationToken.None);
        got.Should().NotBeNull();
        got!.ProfileId.Value.Should().Be("p_a");
        got.Priority.Should().Be(42);
        got.Effect.Should().Be(AclEffect.Allow);
        got.AllowedOperations.Should().HaveFlag(AclOperation.Read);
        got.Description.Should().Be("alice rule");
        got.Scope.Table.Should().Be("users");
    }

    [Fact]
    public async Task Upsert_updates_existing_row()
    {
        await SeedProfileAsync(new ProfileId("p_a"));
        var entry = MakeEntry(new ProfileId("p_a"), priority: 0);
        await _store.UpsertAsync(entry, CancellationToken.None);
        var updated = entry with { Priority = 999, Effect = AclEffect.Deny };

        await _store.UpsertAsync(updated, CancellationToken.None);

        var got = await _store.GetAsync(entry.Id, CancellationToken.None);
        got!.Priority.Should().Be(999);
        got.Effect.Should().Be(AclEffect.Deny);
    }

    [Fact]
    public async Task Delete_removes_row()
    {
        await SeedProfileAsync(new ProfileId("p_a"));
        var entry = MakeEntry(new ProfileId("p_a"));
        await _store.UpsertAsync(entry, CancellationToken.None);

        var removed = await _store.DeleteAsync(new ProfileId("p_a"), entry.Id, CancellationToken.None);
        removed.Should().BeTrue();
        (await _store.GetAsync(entry.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_returns_false_when_profile_does_not_match()
    {
        await SeedProfileAsync(new ProfileId("p_a"));
        await SeedProfileAsync(new ProfileId("p_b"));
        var entry = MakeEntry(new ProfileId("p_a"));
        await _store.UpsertAsync(entry, CancellationToken.None);

        var removed = await _store.DeleteAsync(new ProfileId("p_b"), entry.Id, CancellationToken.None);
        removed.Should().BeFalse();

        (await _store.GetAsync(entry.Id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task Cross_profile_isolation_holds()
    {
        await SeedProfileAsync(new ProfileId("p_a"));
        await SeedProfileAsync(new ProfileId("p_b"));

        await _store.UpsertAsync(MakeEntry(new ProfileId("p_a"), table: "users"), CancellationToken.None);
        await _store.UpsertAsync(MakeEntry(new ProfileId("p_a"), table: "orders"), CancellationToken.None);
        await _store.UpsertAsync(MakeEntry(new ProfileId("p_b"), table: "products"), CancellationToken.None);

        var fromA = await _store.ListAsync(new ProfileId("p_a"), CancellationToken.None);
        var fromB = await _store.ListAsync(new ProfileId("p_b"), CancellationToken.None);

        fromA.Should().HaveCount(2);
        fromA.Select(e => e.Scope.Table).Should().BeEquivalentTo(new[] { "users", "orders" });

        fromB.Should().HaveCount(1);
        fromB[0].Scope.Table.Should().Be("products");
    }

    [Fact]
    public async Task ReplaceAll_swaps_entry_set_for_profile()
    {
        await SeedProfileAsync(new ProfileId("p_a"));
        await _store.UpsertAsync(MakeEntry(new ProfileId("p_a"), table: "old1"), CancellationToken.None);
        await _store.UpsertAsync(MakeEntry(new ProfileId("p_a"), table: "old2"), CancellationToken.None);

        var newSet = new[]
        {
            MakeEntry(new ProfileId("p_a"), table: "new1"),
            MakeEntry(new ProfileId("p_a"), table: "new2"),
            MakeEntry(new ProfileId("p_a"), table: "new3"),
        };
        await _store.ReplaceAllAsync(new ProfileId("p_a"), newSet, CancellationToken.None);

        var listed = await _store.ListAsync(new ProfileId("p_a"), CancellationToken.None);
        listed.Should().HaveCount(3);
        listed.Select(e => e.Scope.Table).Should().BeEquivalentTo(new[] { "new1", "new2", "new3" });
    }

    [Fact]
    public async Task ReplaceAll_rejects_entries_for_other_profile()
    {
        await SeedProfileAsync(new ProfileId("p_a"));
        await SeedProfileAsync(new ProfileId("p_b"));

        Func<Task> act = async () => await _store.ReplaceAllAsync(
            new ProfileId("p_a"),
            new[] { MakeEntry(new ProfileId("p_b")) },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Profile_delete_cascades_to_acl_entries()
    {
        await SeedProfileAsync(new ProfileId("p_doomed"));
        var entry = MakeEntry(new ProfileId("p_doomed"));
        await _store.UpsertAsync(entry, CancellationToken.None);

        await _profiles.DeleteAsync(new ProfileId("p_doomed"), CancellationToken.None);

        (await _store.GetAsync(entry.Id, CancellationToken.None)).Should().BeNull();
    }

    private async Task SeedProfileAsync(ProfileId id)
    {
        await _profiles.UpsertAsync(new Profile(
            id,
            id.Value,
            id.Value,
            Issuer: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Status: ProfileStatus.Active,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal)),
            CancellationToken.None);
    }

    private static AclEntry MakeEntry(
        ProfileId profile,
        int priority = 0,
        string? table = null,
        string? description = null)
    {
        return new AclEntry(
            AclEntryId.NewId(),
            profile,
            AclSubjectKind.Profile,
            new AclObjectScope(null, null, null, "public", table, null),
            AclOperation.Read,
            AclEffect.Allow,
            priority,
            description);
    }
}
