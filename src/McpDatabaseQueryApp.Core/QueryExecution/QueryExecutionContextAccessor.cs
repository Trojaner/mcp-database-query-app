namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Default <see cref="IQueryExecutionContextAccessor"/> that uses an
/// <see cref="AsyncLocal{T}"/> slot. The <see cref="QueryPipeline"/> sets the
/// value before invoking the first step and restores the prior value on
/// completion (including failure).
/// </summary>
public sealed class QueryExecutionContextAccessor : IQueryExecutionContextAccessor
{
    private static readonly AsyncLocal<QueryExecutionContext?> Slot = new();

    /// <inheritdoc />
    public QueryExecutionContext? Current => Slot.Value;

    /// <summary>
    /// Sets the ambient context. Returns a disposable that restores the
    /// previous value on dispose; the pipeline uses this to ensure correct
    /// nesting if a step ever runs nested pipelines.
    /// </summary>
    internal static IDisposable Begin(QueryExecutionContext context)
    {
        var prior = Slot.Value;
        Slot.Value = context;
        return new Restore(prior);
    }

    private sealed class Restore : IDisposable
    {
        private readonly QueryExecutionContext? _prior;
        private bool _disposed;

        public Restore(QueryExecutionContext? prior)
        {
            _prior = prior;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Slot.Value = _prior;
            _disposed = true;
        }
    }
}
