using McpDatabaseQueryApp.Core.Results;

namespace McpDatabaseQueryApp.Server.Pagination;

public static class PageCodec
{
    public static Cursor Decode(string? cursor, int defaultLimit)
    {
        if (CursorCodec.TryDecode(cursor, out var decoded))
        {
            var limit = decoded.Limit > 0 ? decoded.Limit : defaultLimit;
            return new Cursor(decoded.Offset, limit, decoded.Filter, decoded.Sort);
        }

        return new Cursor(0, defaultLimit);
    }

    public static string? EncodeNext(Cursor current, int itemsReturned, long total)
    {
        var nextOffset = current.Offset + itemsReturned;
        return nextOffset < total
            ? CursorCodec.Encode(new Cursor(nextOffset, current.Limit, current.Filter, current.Sort))
            : null;
    }
}
