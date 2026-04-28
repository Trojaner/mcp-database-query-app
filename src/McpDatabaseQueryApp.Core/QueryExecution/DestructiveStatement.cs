using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Description of a single destructive SQL statement, surfaced to confirmers
/// and to callers that classify SQL outside the pipeline. Carries enough
/// context to render a useful confirmation prompt.
/// </summary>
/// <param name="Kind">The parsed <see cref="StatementKind"/>.</param>
/// <param name="Reason">
/// Human-readable explanation of why the statement is considered
/// destructive (e.g. "DELETE without WHERE", "drops a table").
/// </param>
/// <param name="Sql">The original SQL text for the statement, untruncated.</param>
public sealed record DestructiveStatement(StatementKind Kind, string Reason, string Sql);
