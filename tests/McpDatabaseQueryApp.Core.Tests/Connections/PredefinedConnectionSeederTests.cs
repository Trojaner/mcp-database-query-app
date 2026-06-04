using FluentAssertions;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Connections;

public sealed class PredefinedConnectionSeederTests : IAsyncLifetime
{
    private readonly string _tempDir;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly ProfileContextAccessor _profileContext;
    private readonly SqliteMetadataStore _metadata;
    private readonly SqliteProfileStore _profiles;
    private readonly AmbientProfileCredentialProtector _protector;

    public PredefinedConnectionSeederTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mcp-database-query-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);

        _options = new McpDatabaseQueryAppOptions { MetadataDbPath = Path.Combine(_tempDir, "meta.db") };
        _profileContext = new ProfileContextAccessor();
        _metadata = new SqliteMetadataStore(_options, _profileContext);
        _profiles = new SqliteProfileStore(_options);

        var keys = new HkdfProfileKeyProvider(new FixedMasterKey());
        _protector = new AmbientProfileCredentialProtector(new ProfileCredentialProtector(keys), _profileContext);
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

    private PredefinedConnectionSeeder CreateSeeder() => new(
        _options,
        _metadata,
        _protector,
        _profiles,
        _profileContext,
        NullLogger<PredefinedConnectionSeeder>.Instance);

    [Fact]
    public async Task Seeds_configured_connection_with_encrypted_password()
    {
        _options.Connections.Add(new PredefinedConnectionOptions
        {
            Name = "scraper",
            Provider = "Postgres",
            Host = "db.internal",
            Port = 5432,
            Database = "etsy_listings",
            Username = "etsy",
            Password = "etsy_secret",
            SslMode = "Disable",
            ReadOnly = true,
            DefaultSchema = "es",
            Tags = ["scraper"],
        });

        await CreateSeeder().SeedAsync(CancellationToken.None);

        var record = await ReadUnderDefaultProfileAsync(() => _metadata.GetDatabaseAsync("scraper", CancellationToken.None));
        record.Should().NotBeNull();
        record!.Descriptor.Provider.Should().Be(DatabaseKind.Postgres);
        record.Descriptor.Host.Should().Be("db.internal");
        record.Descriptor.Database.Should().Be("etsy_listings");
        record.Descriptor.SslMode.Should().Be("Disable");
        record.Descriptor.DefaultSchema.Should().Be("es");
        record.Descriptor.ReadOnly.Should().BeTrue();
        record.Descriptor.Tags.Should().ContainSingle().Which.Should().Be("scraper");

        // Password round-trips through the same ambient (default) profile.
        using (_profileContext.Begin(await DefaultProfileAsync()))
        {
            _protector.Decrypt(record.PasswordCipher, record.PasswordNonce).Should().Be("etsy_secret");
        }
    }

    [Fact]
    public async Task Reseeding_is_idempotent_and_updates_in_place()
    {
        _options.Connections.Add(new PredefinedConnectionOptions
        {
            Name = "scraper",
            Provider = "Postgres",
            Host = "old.internal",
            Database = "etsy_listings",
            Username = "etsy",
            Password = "etsy_secret",
        });

        await CreateSeeder().SeedAsync(CancellationToken.None);

        _options.Connections[0].Host = "new.internal";
        await CreateSeeder().SeedAsync(CancellationToken.None);

        var (items, total) = await ReadUnderDefaultProfileAsync(
            () => _metadata.ListDatabasesAsync(0, 100, null, CancellationToken.None));
        total.Should().Be(1);
        items.Single().Host.Should().Be("new.internal");
    }

    [Fact]
    public async Task Invalid_entries_are_skipped_without_aborting_valid_ones()
    {
        _options.Connections.Add(new PredefinedConnectionOptions
        {
            Name = "bad-provider",
            Provider = "NotARealProvider",
            Host = "h",
            Database = "d",
            Username = "u",
            Password = "p",
        });
        _options.Connections.Add(new PredefinedConnectionOptions
        {
            Name = "missing-password",
            Provider = "Postgres",
            Host = "h",
            Database = "d",
            Username = "u",
        });
        _options.Connections.Add(new PredefinedConnectionOptions
        {
            Name = "good",
            Provider = "Postgres",
            Host = "h",
            Database = "d",
            Username = "u",
            Password = "p",
        });

        // Must not throw even though two of three entries are invalid.
        await CreateSeeder().SeedAsync(CancellationToken.None);

        var (_, total) = await ReadUnderDefaultProfileAsync(
            () => _metadata.ListDatabasesAsync(0, 100, null, CancellationToken.None));
        total.Should().Be(1);
        (await ReadUnderDefaultProfileAsync(() => _metadata.GetDatabaseAsync("good", CancellationToken.None)))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task No_connections_configured_is_a_noop()
    {
        await CreateSeeder().SeedAsync(CancellationToken.None);

        var (_, total) = await ReadUnderDefaultProfileAsync(
            () => _metadata.ListDatabasesAsync(0, 100, null, CancellationToken.None));
        total.Should().Be(0);
    }

    private async Task<Profile> DefaultProfileAsync() =>
        await _profiles.GetAsync(ProfileId.Default, CancellationToken.None)
        ?? throw new InvalidOperationException("default profile missing");

    private async Task<T> ReadUnderDefaultProfileAsync<T>(Func<Task<T>> read)
    {
        using (_profileContext.Begin(await DefaultProfileAsync()))
        {
            return await read();
        }
    }

    private sealed class FixedMasterKey : IMasterKeyProvider
    {
        public byte[] GetKey()
        {
            var key = new byte[32];
            for (var i = 0; i < key.Length; i++)
            {
                key[i] = (byte)(i & 0xFF);
            }

            return key;
        }
    }
}
