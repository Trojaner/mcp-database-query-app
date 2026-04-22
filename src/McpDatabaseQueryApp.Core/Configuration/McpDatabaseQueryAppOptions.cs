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

    public TimeSpan ConnectionIdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    public bool AutoConnect { get; set; } = true;

    public bool DangerouslySkipPermissions { get; set; }
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
