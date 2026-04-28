using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Pipeline step that enforces data-isolation rules. Runs at
/// <see cref="QueryStepOrder.Rewriting"/> (300) — after parsing and
/// authorization, before the safety steps. Rewrites
/// <see cref="QueryExecutionContext.Sql"/> in place when at least one rule
/// applies and re-parses the result so downstream steps see the post-rewrite
/// AST.
/// </summary>
/// <remarks>
/// <para>Idempotency: when no directives are produced the step takes the fast
/// path — it does not invoke the rewriter, does not re-parse, and does not
/// touch <see cref="QueryExecutionContext.Sql"/>. The reference equality of
/// <see cref="QueryExecutionContext.Parsed"/> is therefore preserved across
/// calls when the rule set is empty.</para>
/// <para>Fail-closed: a rewriter exception is wrapped in
/// <see cref="IsolationRewriteFailedException"/> and aborts the pipeline.
/// We refuse the query rather than execute it without the operator-mandated
/// filter.</para>
/// </remarks>
public sealed class IsolationRuleStep : IQueryExecutionStep
{
    private readonly IIsolationRuleEngine _engine;
    private readonly IQueryRewriterFactory _rewriters;
    private readonly IQueryParserFactory _parsers;
    private readonly ILogger<IsolationRuleStep> _logger;

    /// <summary>
    /// Key under which the step records the directives it applied for
    /// auditing. Value type is <see cref="IReadOnlyList{RewriteDirective}"/>.
    /// </summary>
    public const string AppliedDirectivesItemKey = "IsolationApplied";

    /// <summary>
    /// Creates a new <see cref="IsolationRuleStep"/>.
    /// </summary>
    public IsolationRuleStep(
        IIsolationRuleEngine engine,
        IQueryRewriterFactory rewriters,
        IQueryParserFactory parsers,
        ILogger<IsolationRuleStep> logger)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(rewriters);
        ArgumentNullException.ThrowIfNull(parsers);
        ArgumentNullException.ThrowIfNull(logger);
        _engine = engine;
        _rewriters = rewriters;
        _parsers = parsers;
        _logger = logger;
    }

    /// <inheritdoc />
    public int Order => QueryStepOrder.Rewriting;

    /// <inheritdoc />
    public async Task ExecuteAsync(QueryExecutionContext context, QueryStepDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var parsed = context.Parsed
            ?? throw new InvalidOperationException(
                "IsolationRuleStep requires the parse step to have run first.");

        var directives = await _engine
            .BuildDirectivesAsync(context, cancellationToken)
            .ConfigureAwait(false);

        if (directives.Count == 0)
        {
            await next(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_rewriters.TryGetRewriter(context.Kind, out var rewriter))
        {
            // Rewriter is mandatory once a directive is in flight. Fail closed.
            throw new IsolationRewriteFailedException(
                $"No IQueryRewriter is registered for {context.Kind}; cannot apply {directives.Count} isolation rule(s).");
        }

        string rewrittenSql;
        try
        {
            rewrittenSql = rewriter.Rewrite(parsed, directives);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "Isolation rewriter for {Kind} threw applying {Count} directive(s); aborting pipeline.",
                context.Kind,
                directives.Count);
            throw new IsolationRewriteFailedException(
                $"Failed to apply {directives.Count} isolation rule(s) to {context.Kind} SQL: {ex.Message}",
                ex);
        }

        // Re-parse so downstream steps (Safety, Logging) operate on the
        // post-rewrite AST. Wrap parse failures in IsolationRewriteFailed
        // since the rewrite is what introduced the new SQL.
        ParsedBatch reparsed;
        try
        {
            reparsed = _parsers.GetParser(context.Kind).Parse(rewrittenSql);
        }
        catch (QueryParseException ex)
        {
            _logger.LogError(
                ex,
                "Re-parse of isolation-rewritten SQL failed for {Kind}; aborting pipeline.",
                context.Kind);
            throw new IsolationRewriteFailedException(
                $"Re-parse of rewritten SQL failed for {context.Kind}: {ex.Message}",
                ex);
        }

        context.Sql = rewrittenSql;
        context.Parsed = reparsed;

        // Merge isolation parameters into the request parameters so the
        // tool layer's downstream Dapper invocation sees them. Existing
        // caller-supplied parameters take precedence on collision — the
        // engine's parameter allocator avoids that case anyway.
        if (context.Items.TryGetValue(IsolationRuleEngine.ParametersItemKey, out var raw)
            && raw is IReadOnlyDictionary<string, object?> isolationParameters
            && isolationParameters.Count > 0)
        {
            var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (context.Parameters is not null)
            {
                foreach (var (k, v) in context.Parameters)
                {
                    merged[k] = v;
                }
            }
            foreach (var (k, v) in isolationParameters)
            {
                if (!merged.ContainsKey(k))
                {
                    merged[k] = v;
                }
            }
            context.Parameters = merged;
        }

        context.Items[AppliedDirectivesItemKey] = directives;

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Applied {Count} data-isolation rule(s) to query on connection {ConnectionId}.",
                directives.Count,
                context.ConnectionId);
        }

        await next(cancellationToken).ConfigureAwait(false);
    }
}
