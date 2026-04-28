using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution.Steps;

/// <summary>
/// Routes destructive batches through the registered
/// <see cref="IDestructiveOperationConfirmer"/>. Skipped entirely when:
/// <list type="bullet">
///   <item><description>the parsed batch has no destructive statements;</description></item>
///   <item><description>the caller passed <c>confirm=true</c> AND the host
///   is started with <c>--dangerously-skip-permissions</c>;</description></item>
///   <item><description>the mode is <see cref="QueryExecutionMode.Explain"/>
///   (the underlying connection runs <c>EXPLAIN</c>, not the DML).</description></item>
/// </list>
/// </summary>
public sealed class DestructiveConfirmationStep : IQueryExecutionStep
{
    private readonly IDestructiveOperationConfirmer _confirmer;
    private readonly McpDatabaseQueryAppOptions _options;

    public DestructiveConfirmationStep(
        IDestructiveOperationConfirmer confirmer,
        McpDatabaseQueryAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(confirmer);
        ArgumentNullException.ThrowIfNull(options);
        _confirmer = confirmer;
        _options = options;
    }

    /// <inheritdoc />
    public int Order => QueryStepOrder.Safety;

    /// <inheritdoc />
    public async Task ExecuteAsync(QueryExecutionContext context, QueryStepDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var parsed = context.Parsed
            ?? throw new InvalidOperationException(
                "DestructiveConfirmationStep requires the parse step to have run first.");

        if (context.Mode == QueryExecutionMode.Explain
            || !parsed.ContainsDestructive
            || (context.ConfirmDestructive && _options.DangerouslySkipPermissions))
        {
            await next(cancellationToken).ConfigureAwait(false);
            return;
        }

        var statements = BuildDestructiveStatements(parsed);

        var verdict = await _confirmer.ConfirmAsync(statements, cancellationToken).ConfigureAwait(false);
        if (verdict is null)
        {
            throw new DestructiveOperationConfirmationRequiredException(
                "This SQL contains destructive operations and the connected client does not support elicitation. Re-run with confirm=true on a server started with --dangerously-skip-permissions, or connect a client that supports elicitation.",
                statements);
        }

        if (verdict == false)
        {
            throw new DestructiveOperationCancelledException(
                "Destructive operation declined.",
                statements);
        }

        await next(cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<DestructiveStatement> BuildDestructiveStatements(ParsedBatch parsed)
    {
        var list = new List<DestructiveStatement>(parsed.Statements.Count);
        for (var i = 0; i < parsed.Statements.Count; i++)
        {
            var stmt = parsed.Statements[i];
            if (stmt.IsDestructive)
            {
                list.Add(new DestructiveStatement(
                    stmt.StatementKind,
                    DestructiveReasonFormatter.Format(stmt),
                    stmt.OriginalText));
            }
        }

        return list;
    }
}
