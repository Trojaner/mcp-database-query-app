namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Delegate invoked by an <see cref="IQueryExecutionStep"/> to hand control
/// to the next step in the pipeline. A step short-circuits the pipeline by
/// throwing an exception or simply returning without invoking
/// <c>next</c>.
/// </summary>
public delegate Task QueryStepDelegate(CancellationToken cancellationToken);

/// <summary>
/// Single stage in the chain-of-responsibility query execution pipeline.
/// Implementations are stateless singletons (or transient with no per-step
/// state) and must be safe to invoke concurrently across requests.
/// </summary>
public interface IQueryExecutionStep
{
    /// <summary>
    /// Relative ordering (smaller = earlier). Built-in steps use the
    /// constants on <see cref="QueryStepOrder"/>; custom steps should pick
    /// values that fit between them.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Runs the step. Throw to abort the pipeline; otherwise call
    /// <paramref name="next"/> exactly once to continue.
    /// </summary>
    Task ExecuteAsync(QueryExecutionContext context, QueryStepDelegate next, CancellationToken cancellationToken);
}
