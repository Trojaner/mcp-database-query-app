namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// A column reference within a parsed statement. <see cref="Schema"/> and
/// <see cref="Table"/> may be null when the source object cannot be
/// statically resolved (e.g. <c>SELECT *</c> or unaliased expressions).
/// </summary>
public sealed record ColumnReference(
    string? Schema,
    string? Table,
    string Name,
    ColumnUsage Usage);
