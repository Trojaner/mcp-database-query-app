using FluentAssertions;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Storage;

public sealed class SqliteProfileStoreTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly SqliteMetadataStore _metadata;
    private readonly SqliteProfileStore _profiles;

    public SqliteProfileStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-database-query-app-profile-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _options = new McpDatabaseQueryAppOptions { MetadataDbPath = Path.Combine(_tempDir, "meta.db") };
        _metadata = new SqliteMetadataStore(_options, new ProfileContextAccessor());
        _profiles = new SqliteProfileStore(_options);
    }

    public async Task InitializeAsync()
    {
        // Migrations seed the default profile and create the profiles table.
        await _metadata.InitializeAsync(CancellationToken.None);
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
    public async Task Default_profile_exists_after_migration()
    {
        var dflt = await _profiles.GetAsync(ProfileId.Default, CancellationToken.None);
        dflt.Should().NotBeNull();
        dflt!.Id.Should().Be(ProfileId.Default);
        dflt.Status.Should().Be(ProfileStatus.Active);
    }

    [Fact]
    public async Task Upsert_get_list_round_trip()
    {
        var alice = new Profile(
            new ProfileId("p_alice"),
            "Alice",
            Subject: "alice",
            Issuer: "https://issuer.example",
            CreatedAt: DateTimeOffset.UtcNow,
            Status: ProfileStatus.Active,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal) { ["k"] = "v" });

        await _profiles.UpsertAsync(alice, CancellationToken.None);

        var got = await _profiles.GetAsync(alice.Id, CancellationToken.None);
        got.Should().NotBeNull();
        got!.Name.Should().Be("Alice");
        got.Subject.Should().Be("alice");
        got.Issuer.Should().Be("https://issuer.example");
        got.Metadata.Should().ContainKey("k").WhoseValue.Should().Be("v");

        var list = await _profiles.ListAsync(CancellationToken.None);
        list.Should().Contain(p => p.Id == alice.Id);
        list.Should().Contain(p => p.Id == ProfileId.Default);
    }

    [Fact]
    public async Task Upsert_updates_existing_row()
    {
        var profile = new Profile(
            new ProfileId("p_x"),
            "Original",
            Subject: "x",
            Issuer: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Status: ProfileStatus.Active,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

        await _profiles.UpsertAsync(profile, CancellationToken.None);
        await _profiles.UpsertAsync(profile with { Name = "Renamed", Status = ProfileStatus.Disabled }, CancellationToken.None);

        var got = await _profiles.GetAsync(profile.Id, CancellationToken.None);
        got!.Name.Should().Be("Renamed");
        got.Status.Should().Be(ProfileStatus.Disabled);
    }

    [Fact]
    public async Task FindByIdentity_returns_match()
    {
        var profile = new Profile(
            new ProfileId("p_bob"),
            "Bob",
            Subject: "bob-sub",
            Issuer: "https://idp.example",
            CreatedAt: DateTimeOffset.UtcNow,
            Status: ProfileStatus.Active,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal));
        await _profiles.UpsertAsync(profile, CancellationToken.None);

        var found = await _profiles.FindByIdentityAsync("https://idp.example", "bob-sub", CancellationToken.None);
        found.Should().NotBeNull();
        found!.Id.Should().Be(profile.Id);

        var miss = await _profiles.FindByIdentityAsync("https://other.example", "bob-sub", CancellationToken.None);
        miss.Should().BeNull();
    }

    [Fact]
    public async Task FindByIdentity_handles_null_issuer()
    {
        // Default profile has issuer = NULL.
        var found = await _profiles.FindByIdentityAsync(null, ProfileId.DefaultValue, CancellationToken.None);
        found.Should().NotBeNull();
        found!.Id.Should().Be(ProfileId.Default);
    }

    [Fact]
    public async Task Delete_removes_profile_and_returns_true()
    {
        var profile = new Profile(
            new ProfileId("p_del"),
            "Doomed",
            Subject: "doomed",
            Issuer: null,
            CreatedAt: DateTimeOffset.UtcNow,
            Status: ProfileStatus.Active,
            Metadata: new Dictionary<string, string>(StringComparer.Ordinal));
        await _profiles.UpsertAsync(profile, CancellationToken.None);

        var deleted = await _profiles.DeleteAsync(profile.Id, CancellationToken.None);

        deleted.Should().BeTrue();
        (await _profiles.GetAsync(profile.Id, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_unknown_profile_returns_false()
    {
        var deleted = await _profiles.DeleteAsync(new ProfileId("does-not-exist"), CancellationToken.None);
        deleted.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_default_profile_throws()
    {
        Func<Task> act = async () => await _profiles.DeleteAsync(ProfileId.Default, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await _profiles.GetAsync(ProfileId.Default, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public async Task EnsureDefault_is_idempotent()
    {
        await _profiles.EnsureDefaultAsync(CancellationToken.None);
        await _profiles.EnsureDefaultAsync(CancellationToken.None);
        var list = await _profiles.ListAsync(CancellationToken.None);
        list.Count(p => p.Id == ProfileId.Default).Should().Be(1);
    }
}
