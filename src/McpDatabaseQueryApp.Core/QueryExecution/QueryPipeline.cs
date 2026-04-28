namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Default <see cref="IQueryPipeline"/> implementation. Steps are sorted by
/// <see cref="IQueryExecutionStep.Order"/> on construction; insertion order
/// in DI registration is irrelevant.
/// </summary>
public sealed class QueryPipeline : IQueryPipeline
{
    private readonly IReadOnlyList<IQueryExecutionStep> _steps;

    /// <summary>
    /// Constructs the pipeline from the supplied steps. Stable-sorts by
    /// <see cref="IQueryExecutionStep.Order"/> so deterministic ordering is
    /// preserved for steps that share a value.
    /// </summary>
    public QueryPipeline(IEnumerable<IQueryExecutionStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.OrderBy(static s => s.Order).ToArray();
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(QueryExecutionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        using (QueryExecutionContextAccessor.Begin(context))
        {
            await Invoke(0, context, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task Invoke(int index, QueryExecutionContext context, CancellationToken cancellationToken)
    {
        if (index >= _steps.Count)
        {
            return Task.CompletedTask;
        }

        var step = _steps[index];
        return step.ExecuteAsync(context, ct => Invoke(index + 1, context, ct), cancellationToken);
    }
}
