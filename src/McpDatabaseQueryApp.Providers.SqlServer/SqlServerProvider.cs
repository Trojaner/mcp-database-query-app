using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Providers.SqlServer;

public sealed class SqlServerProvider : IDatabaseProvider
{
    private readonly ILoggerFactory _loggerFactory;

    public SqlServerProvider(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public DatabaseKind Kind => DatabaseKind.SqlServer;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsSchemas: true,
        SupportsExplainJson: true,
        SupportsListenNotify: false,
        SupportsTemporalTables: true,
        SupportsExtensions: false,
        SupportsAgentJobs: true,
        SystemSchemas: ["sys", "INFORMATION_SCHEMA", "guest"],
        SqlKeywords: SqlServerKeywords.All);

    public string BuildConnectionString(ConnectionDescriptor descriptor, string password)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = descriptor.Port is { } port ? $"{descriptor.Host},{port}" : descriptor.Host,
            InitialCatalog = descriptor.Database,
            UserID = descriptor.Username,
            Password = password,
            ApplicationName = "MCP Database Query App",
            TrustServerCertificate = descriptor.TrustServerCertificate,
            ConnectTimeout = 15,
        };

        builder.Encrypt = descriptor.SslMode.ToUpperInvariant() switch
        {
            "DISABLE" or "NONE" or "OPTIONAL" => SqlConnectionEncryptOption.Optional,
            "STRICT" => SqlConnectionEncryptOption.Strict,
            _ => SqlConnectionEncryptOption.Mandatory,
        };

        if (descriptor.ReadOnly)
        {
            builder.ApplicationIntent = ApplicationIntent.ReadOnly;
        }

        return builder.ConnectionString;
    }

    public async Task<IDatabaseConnection> OpenAsync(
        ConnectionDescriptor descriptor,
        string password,
        CancellationToken cancellationToken,
        string? preassignedConnectionId = null)
    {
        var connString = BuildConnectionString(descriptor, password);
        var connection = new SqlConnection(connString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var id = preassignedConnectionId ?? ConnectionIdFactory.NewConnectionId();
        var logger = _loggerFactory.CreateLogger<SqlServerConnection>();
        return new SqlServerConnection(id, descriptor, connection, logger);
    }
}
