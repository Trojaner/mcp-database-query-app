namespace McpDatabaseQueryApp.Core.Configuration;

public sealed class McpDatabaseQueryAppOptions
{
    public const string SectionName = "McpDatabaseQueryApp";

    public string MetadataDbPath { get; set; } = "%APPDATA%/McpDatabaseQueryApp/mcp-database-query-app.db";

    public int DefaultResultLimit { get; set; } = 500;

    public int MaxResultLimit { get; set; } = 50_000;

    public bool AllowDisableLimit { get; set; } = true;

    public bool ReadOnlyByDefault { get; set; } = true;

    public TimeSpan ResultSetTtl { get; set; } = TimeSpan.FromMinutes(10);

    public int SlowQueryThresholdMs { get; set; } = 2_000;

    public TransportOptions Transport { get; set; } = new();

    public UiOptions Ui { get; set; } = new();

    public LoggingOptions Logging { get; set; } = new();

    public SecretsOptions Secrets { get; set; } = new();

    public OAuth2Options OAuth2 { get; set; } = new();

    public TimeSpan ConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public bool AutoConnect { get; set; } = true;

    public bool DangerouslySkipPermissions { get; set; }

    /// <summary>
    /// Pre-defined connections seeded into the metadata store at startup. Lets
    /// headless deployments ship ready-to-use connections via configuration /
    /// environment variables instead of an operator running
    /// <c>db_predefined_create</c> interactively. Seeding is idempotent: each
    /// entry is upserted by <see cref="PredefinedConnectionOptions.Name"/> under
    /// the built-in default profile on every startup.
    /// </summary>
    public IList<PredefinedConnectionOptions> Connections { get; set; } = new List<PredefinedConnectionOptions>();
}

/// <summary>
/// A single pre-defined connection declared in configuration. Mirrors the
/// fields of <see cref="Connections.ConnectionDescriptor"/> plus the plaintext
/// <see cref="Password"/>, which is encrypted before it is written to the
/// metadata store and is never echoed back in any MCP payload.
/// </summary>
public sealed class PredefinedConnectionOptions
{
    /// <summary>Unique connection name; also the idempotency key for seeding.</summary>
    public string? Name { get; set; }

    /// <summary>Provider kind, parsed case-insensitively (e.g. <c>Postgres</c>, <c>SqlServer</c>).</summary>
    public string? Provider { get; set; }

    public string? Host { get; set; }

    public int? Port { get; set; }

    public string? Database { get; set; }

    public string? Username { get; set; }

    /// <summary>Plaintext password. Encrypted at rest; required to seed an entry.</summary>
    public string? Password { get; set; }

    /// <summary>SSL mode; defaults to <c>Require</c> when unset.</summary>
    public string? SslMode { get; set; }

    public bool? TrustServerCertificate { get; set; }

    /// <summary>Whether the seeded connection is read-only. Defaults to <c>true</c>.</summary>
    public bool ReadOnly { get; set; } = true;

    public string? DefaultSchema { get; set; }

    public IList<string> Tags { get; set; } = new List<string>();
}

public sealed class TransportOptions
{
    public StdioTransportOptions Stdio { get; set; } = new();

    public HttpTransportOptions Http { get; set; } = new();
}

public sealed class StdioTransportOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class HttpTransportOptions
{
    public bool Enabled { get; set; }

    public string Urls { get; set; } = "http://127.0.0.1:5218";
}

public sealed class UiOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class LoggingOptions
{
    public bool EmitSqlToMcpClient { get; set; } = true;

    public bool RedactLiteralsInLogs { get; set; } = true;
}

public sealed class SecretsOptions
{
    public string KeyRef { get; set; } = "UserSecrets:McpDatabaseQueryApp:MasterKey";
}

/// <summary>
/// OAuth2 / OIDC validation options for the HTTP transport. When
/// <see cref="Authority"/> is unset the HTTP transport disables JWT bearer
/// validation entirely and every request resolves to the built-in default
/// profile.
/// </summary>
public sealed class OAuth2Options
{
    /// <summary>OIDC authority (issuer URL) used to discover signing keys.</summary>
    public string? Authority { get; set; }

    /// <summary>Required <c>aud</c> claim value, if any.</summary>
    public string? Audience { get; set; }

    /// <summary>Whether HTTPS is required when fetching authority metadata.</summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>Override metadata address (defaults to <c>{Authority}/.well-known/openid-configuration</c>).</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>
    /// When true (default), unknown <c>(issuer, subject)</c> pairs auto-create
    /// a profile on first sight. When false, unknown identities are rejected
    /// with HTTP 403.
    /// </summary>
    public bool AutoProvisionProfiles { get; set; } = true;
}
