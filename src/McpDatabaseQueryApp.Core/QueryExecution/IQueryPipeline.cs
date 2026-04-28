namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Composes the registered <see cref="IQueryExecutionStep"/> instances into a
/// single chain-of-responsibility pipeline. Tools call
/// <see cref="ExecuteAsync"/> before forwarding the (possibly rewritten) SQL
/// to the underlying database connection.
/// </summary>
public interface IQueryPipeline
{
    /// <summary>
    /// Runs every registered step in order. Throws if any step rejects the
    /// request; otherwise the context returns mutated as the steps left it
    /// (e.g. with <see cref="QueryExecutionContext.Parsed"/> populated).
    /// </summary>
    Task ExecuteAsync(QueryExecutionContext context, CancellationToken cancellationToken);
}
