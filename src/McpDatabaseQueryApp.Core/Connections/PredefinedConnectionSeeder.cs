using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Core.Connections;

/// <summary>
/// Seeds pre-defined database connections declared in process configuration
/// (<c>McpDatabaseQueryApp:Connections</c>) into the SQLite metadata store at
/// startup. This lets headless deployments — an MCP server launched by an
/// orchestrator with no interactive operator — ship with ready-to-use
/// connections instead of requiring a human to invoke
/// <c>db_predefined_create</c>.
/// </summary>
/// <remarks>
/// <para>
/// Seeding is idempotent: each entry is upserted by name under the built-in
/// default profile, so restarting the process refreshes existing rows rather
/// than accumulating duplicates (the metadata store keys its upsert on
/// <c>(profile_id, name)</c>).
/// </para>
/// <para>
/// Passwords are encrypted through <see cref="ICredentialProtector"/> exactly
/// like the interactive <c>db_predefined_create</c> path; plaintext never
/// reaches the metadata row. Because the credential key is derived from the
/// ambient profile, seeding runs inside the default-profile scope so the
/// encrypted blobs match what the stdio runtime later decrypts.
/// </para>
/// <para>
/// A malformed or un-encryptable entry is logged and skipped rather than
/// aborting startup, so one bad connection never prevents the server from
/// answering the MCP initialization handshake.
/// </para>
/// </remarks>
public sealed class PredefinedConnectionSeeder
{
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly IMetadataStore _metadata;
    private readonly ICredentialProtector _protector;
    private readonly IProfileStore _profiles;
    private readonly IProfileContextAccessor _profileContext;
    private readonly ILogger<PredefinedConnectionSeeder> _logger;

    public PredefinedConnectionSeeder(
        McpDatabaseQueryAppOptions options,
        IMetadataStore metadata,
        ICredentialProtector protector,
        IProfileStore profiles,
        IProfileContextAccessor profileContext,
        ILogger<PredefinedConnectionSeeder> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(profileContext);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _metadata = metadata;
        _protector = protector;
        _profiles = profiles;
        _profileContext = profileContext;
        _logger = logger;
    }

    /// <summary>
    /// Upserts every configured connection into the metadata store. No-op when
    /// no connections are configured. Must be called after the metadata store
    /// has been initialized and the default profile has been ensured.
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var entries = _options.Connections;
        if (entries is null || entries.Count == 0)
        {
            return;
        }

        var defaultProfile = await _profiles.GetAsync(ProfileId.Default, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                "Default profile is missing; the metadata store must be initialized before seeding connections.");

        // Credentials are encrypted with a per-profile key derived from the
        // ambient profile, and the metadata store stamps the ambient profile id
        // into each row. Seed under the default profile so both match the scope
        // the stdio runtime opens for the process lifetime.
        using var scope = _profileContext.Begin(defaultProfile);

        var seededNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seeded = 0;

        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];

            ConnectionDescriptor descriptor;
            string password;
            try
            {
                (descriptor, password) = BuildDescriptor(entry, index);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Skipping pre-defined connection at index {Index} (name '{Name}'): {Message}",
                    index,
                    entry?.Name,
                    ex.Message);
                continue;
            }

            if (!seededNames.Add(descriptor.Name))
            {
                _logger.LogWarning(
                    "Duplicate pre-defined connection name '{Name}' at index {Index}; keeping the first occurrence and skipping this one.",
                    descriptor.Name,
                    index);
                continue;
            }

            try
            {
                var (cipher, nonce) = _protector.Encrypt(password);
                await _metadata.UpsertDatabaseAsync(descriptor, cipher, nonce, cancellationToken).ConfigureAwait(false);
                seeded++;
                _logger.LogInformation(
                    "Seeded pre-defined connection '{Name}' ({Provider} {Host}:{Port}/{Database}, read-only={ReadOnly}).",
                    descriptor.Name,
                    descriptor.Provider,
                    descriptor.Host,
                    descriptor.Port?.ToString() ?? "default",
                    descriptor.Database,
                    descriptor.ReadOnly);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to persist pre-defined connection '{Name}' at index {Index}; skipping. Is McpDatabaseQueryApp:Secrets:KeyRef configured with a valid master key?",
                    descriptor.Name,
                    index);
            }
        }

        if (seeded > 0)
        {
            _logger.LogInformation("Seeded {Count} pre-defined connection(s) from configuration.", seeded);
        }
    }

    private static (ConnectionDescriptor Descriptor, string Password) BuildDescriptor(
        PredefinedConnectionOptions entry,
        int index)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var name = Require(entry.Name, "Name", index);
        var providerText = Require(entry.Provider, "Provider", index);
        if (!Enum.TryParse<DatabaseKind>(providerText, ignoreCase: true, out var provider))
        {
            throw new InvalidOperationException(
                $"Unknown provider '{providerText}'. Expected one of: {string.Join(", ", Enum.GetNames<DatabaseKind>())}.");
        }

        var host = Require(entry.Host, "Host", index);
        var database = Require(entry.Database, "Database", index);
        var username = Require(entry.Username, "Username", index);
        if (string.IsNullOrEmpty(entry.Password))
        {
            throw new InvalidOperationException("'Password' is required to seed a pre-defined connection.");
        }

        var descriptor = new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = name,
            Provider = provider,
            Host = host,
            Port = entry.Port,
            Database = database,
            Username = username,
            SslMode = string.IsNullOrWhiteSpace(entry.SslMode) ? "Require" : entry.SslMode,
            TrustServerCertificate = entry.TrustServerCertificate ?? false,
            ReadOnly = entry.ReadOnly,
            DefaultSchema = entry.DefaultSchema,
            Tags = entry.Tags is { Count: > 0 } ? [.. entry.Tags] : [],
        };

        return (descriptor, entry.Password);
    }

    private static string Require(string? value, string field, int index)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"'{field}' is required for the pre-defined connection at index {index}.");
        }

        return value;
    }
}
