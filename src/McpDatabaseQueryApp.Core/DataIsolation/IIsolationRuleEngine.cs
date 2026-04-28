using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Builds <see cref="RewriteDirective"/>s from the rules that apply to a
/// pipeline invocation. Wraps <see cref="IIsolationRuleStore"/> so the
/// pipeline step does not need to know about persistence or scope-matching.
/// </summary>
public interface IIsolationRuleEngine
{
    /// <summary>
    /// Walks <see cref="QueryExecutionContext.Parsed"/>, finds every rule
    /// whose <see cref="IsolationScope"/> matches both the connection and
    /// at least one touched table, and returns the corresponding directive
    /// list. Returns an empty list when no rule applies.
    /// </summary>
    Task<IReadOnlyList<RewriteDirective>> BuildDirectivesAsync(
        QueryExecutionContext context,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops any cached rule snapshots so subsequent
    /// <see cref="BuildDirectivesAsync"/> calls re-read the underlying store.
    /// Called by the admin REST API after a rule mutation. The default
    /// implementation is a no-op.
    /// </summary>
    void InvalidateAll() { }
}
