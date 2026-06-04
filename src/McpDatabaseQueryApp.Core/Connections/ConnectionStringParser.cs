using System.Data.Common;

namespace McpDatabaseQueryApp.Core.Connections;

/// <summary>
/// Parses a raw ADO.NET connection string into the discrete fields stored on a
/// <see cref="ConnectionDescriptor"/>. Lives in Core and uses only
/// <see cref="DbConnectionStringBuilder"/> from the BCL, so it pulls in no
/// database driver and honors the Core layering rule. Keyword synonyms for
/// PostgreSQL (Npgsql) and SQL Server (Microsoft.Data.SqlClient) are handled
/// here.
/// </summary>
/// <remarks>
/// Only the keywords the providers rebuild from a descriptor are extracted
/// (host, port, database, user, password, SSL intent). Any other keyword in
/// the source string is intentionally dropped, matching how the app already
/// persists connections as discrete fields rather than a verbatim string.
/// </remarks>
public static class ConnectionStringParser
{
    public sealed record ParsedConnectionString(
        DatabaseKind? InferredKind,
        string? Host,
        int? Port,
        string? Database,
        string? Username,
        string? Password,
        string? SslMode,
        bool? TrustServerCertificate);

    public static ParsedConnectionString Parse(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // DbConnectionStringBuilder throws on malformed input; let it surface
        // so the seeder can log-and-skip the offending entry.
        var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };

        var hasHostKeyword = ContainsAny(builder, "Host", "Hostname");
        var server = Get(builder, "Server", "Data Source", "Address", "Addr", "Network Address");
        var host = Get(builder, "Host", "Hostname") ?? server;

        var kind = InferKind(hasHostKeyword, server);

        int? port = null;
        if (Get(builder, "Port") is { } portText && int.TryParse(portText, out var explicitPort))
        {
            port = explicitPort;
        }
        else if (kind == DatabaseKind.SqlServer && host is { } dataSource
                 && dataSource.Contains(',', StringComparison.Ordinal))
        {
            // SQL Server encodes the port inside Data Source as "host,1433".
            var parts = dataSource.Split(',', 2);
            host = parts[0].Trim();
            if (int.TryParse(parts[1].Trim(), out var embeddedPort))
            {
                port = embeddedPort;
            }
        }

        var database = Get(builder, "Database", "Initial Catalog");
        var username = Get(builder, "Username", "User ID", "User Id", "UserId", "User Name", "Uid", "User");
        var password = Get(builder, "Password", "Pwd");
        var trust = ParseBool(Get(builder, "Trust Server Certificate", "TrustServerCertificate"));

        var sslMode = kind == DatabaseKind.SqlServer
            ? MapSqlServerEncrypt(Get(builder, "Encrypt"))
            : Get(builder, "SSL Mode", "SslMode", "Ssl Mode");

        return new ParsedConnectionString(kind, host, port, database, username, password, sslMode, trust);
    }

    private static DatabaseKind? InferKind(bool hasHostKeyword, string? server)
    {
        // Agreed heuristic: a Host keyword means PostgreSQL; a Server / Data
        // Source keyword without Host means SQL Server; anything else is
        // ambiguous and left for the caller to default.
        if (hasHostKeyword)
        {
            return DatabaseKind.Postgres;
        }

        return string.IsNullOrWhiteSpace(server) ? null : DatabaseKind.SqlServer;
    }

    private static string? MapSqlServerEncrypt(string? encrypt)
    {
        if (string.IsNullOrWhiteSpace(encrypt))
        {
            return null;
        }

        return encrypt.Trim().ToUpperInvariant() switch
        {
            "FALSE" or "NO" or "OPTIONAL" or "0" => "Disable",
            "STRICT" => "Strict",
            _ => "Require",
        };
    }

    private static string? Get(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.TryGetValue(key, out var value) && value is not null)
            {
                var text = value.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text.Trim();
                }
            }
        }

        return null;
    }

    private static bool ContainsAny(DbConnectionStringBuilder builder, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (builder.ContainsKey(key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool? ParseBool(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Trim().ToUpperInvariant() switch
        {
            "TRUE" or "YES" or "1" => true,
            "FALSE" or "NO" or "0" => false,
            _ => null,
        };
    }
}
