using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace McpDatabaseQueryApp.Providers.Postgres;

public sealed class PostgresProvider : IDatabaseProvider
{
    private readonly ILoggerFactory _loggerFactory;

    public PostgresProvider(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
    }

    public DatabaseKind Kind => DatabaseKind.Postgres;

    public ProviderCapabilities Capabilities { get; } = new(
        SupportsSchemas: true,
        SupportsExplainJson: true,
        SupportsListenNotify: true,
        SupportsTemporalTables: false,
        SupportsExtensions: true,
        SupportsAgentJobs: false,
        SystemSchemas: ["pg_catalog", "pg_toast", "information_schema"],
        SqlKeywords: PostgresKeywords.All);

    public string BuildConnectionString(ConnectionDescriptor descriptor, string password)
    {
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = descriptor.Host,
            Database = descriptor.Database,
            Username = descriptor.Username,
            Password = password,
            ApplicationName = "MCP Database Query App",
            IncludeErrorDetail = true,
        };

        if (descriptor.Port is { } port)
        {
            builder.Port = port;
        }

        builder.SslMode = descriptor.SslMode.ToUpperInvariant() switch
        {
            "DISABLE" or "NONE" => SslMode.Disable,
            "ALLOW" => SslMode.Allow,
            "PREFER" => SslMode.Prefer,
            "REQUIRE" => SslMode.Require,
            "VERIFY-CA" or "VERIFYCA" => SslMode.VerifyCA,
            "VERIFY-FULL" or "VERIFYFULL" => SslMode.VerifyFull,
            _ => SslMode.Prefer,
        };

        return builder.ConnectionString;
    }

    public async Task<IDatabaseConnection> OpenAsync(
        ConnectionDescriptor descriptor,
        string password,
        CancellationToken cancellationToken,
        string? preassignedConnectionId = null)
    {
        var connString = BuildConnectionString(descriptor, password);
        var connection = new NpgsqlConnection(connString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (descriptor.ReadOnly)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SET default_transaction_read_only = on;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var id = preassignedConnectionId ?? ConnectionIdFactory.NewConnectionId();
        var logger = _loggerFactory.CreateLogger<PostgresConnection>();
        return new PostgresConnection(id, descriptor, connection, logger);
    }
}
