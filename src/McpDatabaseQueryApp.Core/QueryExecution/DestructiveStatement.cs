using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Description of a single state-changing SQL statement, surfaced to
/// confirmers and to callers that classify SQL outside the pipeline. Carries
/// enough context to render a useful confirmation prompt.
/// </summary>
/// <param name="Kind">The parsed <see cref="StatementKind"/>.</param>
/// <param name="Reason">
/// Human-readable explanation of what the statement changes (e.g. "DELETE
/// without WHERE", "creates a table").
/// </param>
/// <param name="Sql">The original SQL text for the statement, untruncated.</param>
/// <param name="IsDestructive">
/// True when the statement is irreversible or unbounded (DROP, TRUNCATE,
/// unqualified DELETE/UPDATE). False for ordinary writes such as INSERT or
/// CREATE, which still require confirmation but are flagged less loudly.
/// </param>
public sealed record DestructiveStatement(
    StatementKind Kind,
    string Reason,
    string Sql,
    bool IsDestructive = true);
