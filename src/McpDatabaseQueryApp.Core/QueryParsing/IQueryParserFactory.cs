namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Selects an <see cref="IQueryParser"/> for a given <see cref="DatabaseKind"/>.
/// Implementations must be thread-safe.
/// </summary>
public interface IQueryParserFactory
{
    IQueryParser GetParser(DatabaseKind kind);

    bool TryGetParser(DatabaseKind kind, out IQueryParser parser);
}
