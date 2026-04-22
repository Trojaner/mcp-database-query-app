using System.Text.RegularExpressions;

namespace McpDatabaseQueryApp.Core.Security;

public static partial class ConnectionStringRedactor
{
    private static readonly string[] SecretKeys =
    [
        "password",
        "pwd",
        "secret",
        "apikey",
        "api_key",
        "accesskey",
        "access_key",
        "accountkey",
        "account_key",
        "token",
    ];

    public static string Redact(string? connectionString)
    {
        if (string.IsNullOrEmpty(connectionString))
        {
            return string.Empty;
        }

        var parts = connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            var key = parts[i][..eq].Trim();
            foreach (var secret in SecretKeys)
            {
                if (key.Equals(secret, StringComparison.OrdinalIgnoreCase)
                    || key.Replace(" ", string.Empty, StringComparison.Ordinal).Equals(secret, StringComparison.OrdinalIgnoreCase))
                {
                    parts[i] = $"{key}=***";
                    break;
                }
            }
        }

        return string.Join(';', parts);
    }
}
