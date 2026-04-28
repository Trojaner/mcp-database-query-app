namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Ambient access to the <see cref="QueryExecutionContext"/> currently being
/// processed by <see cref="IQueryPipeline"/>. Backed by
/// <see cref="System.Threading.AsyncLocal{T}"/> so async/await flows through
/// nested calls and steps naturally.
/// </summary>
/// <remarks>
/// The pipeline implementation is responsible for setting and clearing the
/// ambient value around the step chain. Confirmers, loggers and other
/// extensions read it to recover information that lives on the context
/// (e.g. transport-specific handles in <see cref="QueryExecutionContext.Items"/>).
/// </remarks>
public interface IQueryExecutionContextAccessor
{
    /// <summary>The current pipeline context, or <c>null</c> when no pipeline is running on this execution context.</summary>
    QueryExecutionContext? Current { get; }
}
