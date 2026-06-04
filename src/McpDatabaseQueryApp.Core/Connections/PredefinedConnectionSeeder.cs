using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Core.Connections;

/// <summary>
/// Seeds pre-defined database connections declared in process configuration
/// into the SQLite metadata store at startup. This lets headless deployments —
/// an MCP server launched by an orchestrator with no interactive operator —
/// ship with ready-to-use connections instead of requiring a human to invoke
/// <c>db_predefined_create</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two configuration shapes are supported, both under the app's own section:
/// <list type="bullet">
///   <item><description><c>McpDatabaseQueryApp:Connections</c> — a list of
///   structured entries (discrete fields and/or a per-entry connection
///   string, with full control over read-only, default schema, provider and
///   tags).</description></item>
///   <item><description><c>McpDatabaseQueryApp:ConnectionStrings</c> — a
///   name → connection-string map (read-only, provider inferred from the
///   keywords). Namespaced so it never inherits a host's top-level
///   <c>ConnectionStrings</c>.</description></item>
/// </list>
/// </para>
/// <para>
/// Seeding is idempotent: each entry is upserted by name under the built-in
/// default profile, so restarting refreshes existing rows rather than
/// accumulating duplicates. Passwords are encrypted through
/// <see cref="ICredentialProtector"/> exactly like the interactive
/// <c>db_predefined_create</c> path; plaintext never reaches the metadata row.
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
    /// nothing is configured. Must be called after the metadata store has been
    /// initialized and the default profile has been ensured.
    /// </summary>
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var raws = CollectRaws();
        if (raws.Count == 0)
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

        for (var index = 0; index < raws.Count; index++)
        {
            var raw = raws[index];

            ConnectionDescriptor descriptor;
            string password;
            try
            {
                (descriptor, password) = BuildDescriptor(raw, index);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Skipping pre-defined connection from {Source} (name '{Name}'): {Message}",
                    raw.Source,
                    raw.Name,
                    ex.Message);
                continue;
            }

            if (!seededNames.Add(descriptor.Name))
            {
                _logger.LogWarning(
                    "Duplicate pre-defined connection name '{Name}' from {Source}; keeping the first occurrence and skipping this one.",
                    descriptor.Name,
                    raw.Source);
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
                    "Failed to persist pre-defined connection '{Name}'; skipping. Is McpDatabaseQueryApp:Secrets:KeyRef configured with a valid master key?",
                    descriptor.Name);
            }
        }

        if (seeded > 0)
        {
            _logger.LogInformation("Seeded {Count} pre-defined connection(s) from configuration.", seeded);
        }
    }

    private List<RawSeed> CollectRaws()
    {
        var raws = new List<RawSeed>();

        if (_options.Connections is { Count: > 0 } connections)
        {
            foreach (var entry in connections)
            {
                raws.Add(new RawSeed(
                    Source: "Connections",
                    Name: entry?.Name,
                    Provider: entry?.Provider,
                    ConnectionString: entry?.ConnectionString,
                    Host: entry?.Host,
                    Port: entry?.Port,
                    Database: entry?.Database,
                    Username: entry?.Username,
                    Password: entry?.Password,
                    SslMode: entry?.SslMode,
                    TrustServerCertificate: entry?.TrustServerCertificate,
                    ReadOnly: entry?.ReadOnly ?? true,
                    DefaultSchema: entry?.DefaultSchema,
                    Tags: entry?.Tags is { Count: > 0 } tags ? [.. tags] : []));
            }
        }

        if (_options.ConnectionStrings is { Count: > 0 } connectionStrings)
        {
            foreach (var (name, connectionString) in connectionStrings)
            {
                raws.Add(new RawSeed(
                    Source: "ConnectionStrings",
                    Name: name,
                    Provider: null,
                    ConnectionString: connectionString,
                    Host: null,
                    Port: null,
                    Database: null,
                    Username: null,
                    Password: null,
                    SslMode: null,
                    TrustServerCertificate: null,
                    ReadOnly: true,
                    DefaultSchema: null,
                    Tags: []));
            }
        }

        return raws;
    }

    private static (ConnectionDescriptor Descriptor, string Password) BuildDescriptor(RawSeed raw, int index)
    {
        var name = Require(raw.Name, "Name", index);

        ConnectionStringParser.ParsedConnectionString? parsed = null;
        if (!string.IsNullOrWhiteSpace(raw.ConnectionString))
        {
            parsed = ConnectionStringParser.Parse(raw.ConnectionString);
        }

        var provider = ResolveProvider(raw.Provider, parsed);

        var host = Coalesce(raw.Host, parsed?.Host);
        var port = raw.Port ?? parsed?.Port;
        var database = Coalesce(raw.Database, parsed?.Database);
        var username = Coalesce(raw.Username, parsed?.Username);
        var password = Coalesce(raw.Password, parsed?.Password);
        var sslMode = Coalesce(raw.SslMode, parsed?.SslMode) ?? NaturalSslDefault(provider);
        var trust = raw.TrustServerCertificate ?? parsed?.TrustServerCertificate ?? false;

        var descriptor = new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = name,
            Provider = provider,
            Host = RequireResolved(host, "Host", name),
            Port = port,
            Database = RequireResolved(database, "Database", name),
            Username = RequireResolved(username, "Username", name),
            SslMode = sslMode,
            TrustServerCertificate = trust,
            ReadOnly = raw.ReadOnly,
            DefaultSchema = raw.DefaultSchema,
            Tags = raw.Tags,
        };

        if (string.IsNullOrEmpty(password))
        {
            throw new InvalidOperationException(
                $"A password is required to seed connection '{name}' (set it as a discrete field or in the connection string).");
        }

        return (descriptor, password);
    }

    private static DatabaseKind ResolveProvider(string? providerText, ConnectionStringParser.ParsedConnectionString? parsed)
    {
        if (!string.IsNullOrWhiteSpace(providerText))
        {
            if (!Enum.TryParse<DatabaseKind>(providerText, ignoreCase: true, out var explicitProvider))
            {
                throw new InvalidOperationException(
                    $"Unknown provider '{providerText}'. Expected one of: {string.Join(", ", Enum.GetNames<DatabaseKind>())}.");
            }

            return explicitProvider;
        }

        if (parsed is null)
        {
            throw new InvalidOperationException(
                "'Provider' is required unless a connection string is supplied to infer it from.");
        }

        // Ambiguous connection strings fall back to Postgres by agreement.
        return parsed.InferredKind ?? DatabaseKind.Postgres;
    }

    private static string NaturalSslDefault(DatabaseKind provider) =>
        provider == DatabaseKind.SqlServer ? "Require" : "Prefer";

    private static string? Coalesce(string? primary, string? fallback) =>
        !string.IsNullOrWhiteSpace(primary) ? primary
        : !string.IsNullOrWhiteSpace(fallback) ? fallback
        : null;

    private static string Require(string? value, string field, int index)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"'{field}' is required for the pre-defined connection at index {index}.");
        }

        return value;
    }

    private static string RequireResolved(string? value, string field, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"'{field}' could not be resolved for connection '{name}' from its fields or connection string.");
        }

        return value;
    }

    private readonly record struct RawSeed(
        string Source,
        string? Name,
        string? Provider,
        string? ConnectionString,
        string? Host,
        int? Port,
        string? Database,
        string? Username,
        string? Password,
        string? SslMode,
        bool? TrustServerCertificate,
        bool ReadOnly,
        string? DefaultSchema,
        IReadOnlyList<string> Tags);
}
