namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Strategy interface implemented once per <see cref="DatabaseKind"/>.
/// Implementations MUST be thread-safe and stateless — parsers are kept
/// alive as singletons.
/// </summary>
public interface IQueryParser
{
    /// <summary>
    /// Dialect this parser handles. The registry uses this for dispatch.
    /// </summary>
    DatabaseKind Kind { get; }

    /// <summary>
    /// Parses a SQL batch and returns the structured result. For hard
    /// syntax errors that prevent producing any statement, throw
    /// <see cref="QueryParseException"/>; otherwise surface errors via
    /// <see cref="ParsedBatch.Errors"/>.
    /// </summary>
    ParsedBatch Parse(string sql);
}
