using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution.Steps;

/// <summary>
/// First step in the pipeline. Resolves the dialect-specific
/// <see cref="IQueryParser"/> and stashes the resulting
/// <see cref="ParsedBatch"/> on the context for downstream steps. Hard parse
/// failures are translated into <see cref="QuerySyntaxException"/> and
/// short-circuit the pipeline.
/// </summary>
public sealed class ParseQueryStep : IQueryExecutionStep
{
    private readonly IQueryParserFactory _parsers;

    public ParseQueryStep(IQueryParserFactory parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        _parsers = parsers;
    }

    /// <inheritdoc />
    public int Order => QueryStepOrder.Parse;

    /// <inheritdoc />
    public Task ExecuteAsync(QueryExecutionContext context, QueryStepDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var parser = _parsers.GetParser(context.Kind);
        try
        {
            context.Parsed = parser.Parse(context.Sql);
        }
        catch (QueryParseException ex)
        {
            throw new QuerySyntaxException(
                context.Kind,
                $"Failed to parse {context.Kind} SQL: {ex.Message}",
                ex);
        }

        return next(cancellationToken);
    }
}
