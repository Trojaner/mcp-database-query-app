using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution.Steps;

/// <summary>
/// Enforces two read-only invariants:
/// <list type="bullet">
///   <item><description>A connection flagged <c>IsReadOnly</c> never sees a mutating statement.</description></item>
///   <item><description><c>db_query</c> (Mode = <see cref="QueryExecutionMode.Read"/>) never sees a mutating statement.</description></item>
/// </list>
/// Runs immediately before <see cref="DestructiveConfirmationStep"/> so that
/// a doomed batch is rejected before the user is asked to confirm anything.
/// </summary>
public sealed class ReadOnlyEnforcementStep : IQueryExecutionStep
{
    private const int TruncationLength = 200;

    /// <inheritdoc />
    public int Order => QueryStepOrder.Safety - 1;

    /// <inheritdoc />
    public Task ExecuteAsync(QueryExecutionContext context, QueryStepDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var parsed = context.Parsed
            ?? throw new InvalidOperationException(
                "ReadOnlyEnforcementStep requires the parse step to have run first.");

        if (parsed.ContainsMutation)
        {
            if (context.Connection.IsReadOnly)
            {
                var offending = CollectMutationDescriptions(parsed);
                throw new ReadOnlyConnectionViolationException(
                    $"Connection '{context.ConnectionId}' is read-only; refused {offending.Count} mutating statement(s).",
                    offending);
            }

            if (context.Mode == QueryExecutionMode.Read)
            {
                var offending = CollectMutationDescriptions(parsed);
                throw new MutationOnReadPathException(
                    $"db_query refused {offending.Count} mutating statement(s); use db_execute for INSERT/UPDATE/DELETE/DDL.",
                    offending);
            }
        }

        return next(cancellationToken);
    }

    private static IReadOnlyList<string> CollectMutationDescriptions(ParsedBatch parsed)
    {
        var list = new List<string>(parsed.Statements.Count);
        for (var i = 0; i < parsed.Statements.Count; i++)
        {
            var stmt = parsed.Statements[i];
            if (!stmt.IsMutation)
            {
                continue;
            }

            var text = stmt.OriginalText.Length > TruncationLength
                ? stmt.OriginalText[..TruncationLength] + "..."
                : stmt.OriginalText;
            list.Add($"{stmt.StatementKind}: {text}");
        }

        return list;
    }
}
