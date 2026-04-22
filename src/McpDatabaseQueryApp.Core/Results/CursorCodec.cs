using System.Text;
using System.Text.Json;

namespace McpDatabaseQueryApp.Core.Results;

public static class CursorCodec
{
    public static string Encode(Cursor cursor)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(cursor);
        return Base64UrlEncode(json);
    }

    public static Cursor Decode(string encoded)
    {
        ArgumentException.ThrowIfNullOrEmpty(encoded);
        var json = Base64UrlDecode(encoded);
        return JsonSerializer.Deserialize<Cursor>(json);
    }

    public static bool TryDecode(string? encoded, out Cursor cursor)
    {
        cursor = default;
        if (string.IsNullOrEmpty(encoded))
        {
            return false;
        }

        try
        {
            cursor = Decode(encoded);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] data)
    {
        var base64 = Convert.ToBase64String(data);
        return base64.Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }
}

public readonly record struct Cursor(int Offset, int Limit, string? Filter = null, string? Sort = null);
