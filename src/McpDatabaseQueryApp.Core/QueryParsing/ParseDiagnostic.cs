namespace McpDatabaseQueryApp.Core.QueryParsing;

public enum ParseSeverity
{
    Warning,
    Error,
}

/// <summary>
/// A soft warning or recoverable error surfaced by a parser. Hard syntax
/// failures throw <see cref="QueryParseException"/> instead.
/// </summary>
public sealed record ParseDiagnostic(
    ParseSeverity Severity,
    string Message,
    SourceRange Range,
    string? Code = null);
