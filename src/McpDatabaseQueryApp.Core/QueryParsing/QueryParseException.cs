namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Thrown for hard parser failures (e.g. catastrophic syntax errors,
/// unsupported dialects). Recoverable errors are surfaced through
/// <see cref="ParsedBatch.Errors"/> instead.
/// </summary>
public sealed class QueryParseException : Exception
{
    public QueryParseException(string message)
        : base(message)
    {
        Diagnostics = [];
    }

    public QueryParseException(string message, Exception innerException)
        : base(message, innerException)
    {
        Diagnostics = [];
    }

    public QueryParseException(string message, IReadOnlyList<ParseDiagnostic> diagnostics)
        : base(message)
    {
        Diagnostics = diagnostics;
    }

    public IReadOnlyList<ParseDiagnostic> Diagnostics { get; }
}
