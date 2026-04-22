using System.Security.Cryptography;

namespace McpDatabaseQueryApp.Core.Connections;

public static class ConnectionIdFactory
{
    public static string NewConnectionId() => Build("conn");

    public static string NewDatabaseId() => Build("db");

    public static string NewScriptId() => Build("script");

    public static string NewResultSetId() => Build("result");

    public static string NewNoteId() => Build("note");

    private static string Build(string prefix)
    {
        Span<byte> buffer = stackalloc byte[6];
        RandomNumberGenerator.Fill(buffer);
        return $"{prefix}_{Convert.ToHexString(buffer).ToLowerInvariant()}";
    }
}
