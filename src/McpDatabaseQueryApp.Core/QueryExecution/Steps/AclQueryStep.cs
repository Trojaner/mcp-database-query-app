using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution.Steps;

/// <summary>
/// Pipeline step that enforces the per-profile ACL on every parsed action in
/// the batch. Runs at <see cref="QueryStepOrder.Authorization"/> (slot 200),
/// after the parse step has populated <see cref="QueryExecutionContext.Parsed"/>
/// and before the rewriter / safety steps see the SQL.
/// </summary>
/// <remarks>
/// <para><strong>Behaviour.</strong> For each
/// <see cref="ParsedQueryAction"/> the step builds an
/// <see cref="AclEvaluationRequest"/> from the action's target object plus
/// the ambient <see cref="QueryExecutionContext.Profile"/> and
/// <see cref="QueryExecutionContext.Connection"/> descriptor, then invokes
/// the registered <see cref="IAclEvaluator"/>. A <see cref="AclEffect.Deny"/>
/// outcome throws an <see cref="AccessDeniedException"/> and aborts the
/// pipeline.</para>
/// <para><strong>Column-level checks.</strong> For every projected, filtered,
/// modified or inserted column on a parsed action, the step performs an
/// additional evaluation with the column field populated and the operation
/// derived from the column's usage (read for projected/filtered, the action's
/// operation for modified/inserted). The column form is only invoked when the
/// column reference resolves to an explicit table name; loose <c>SELECT *</c>
/// expansions and unaliased expressions are not column-checked because they
/// cannot be statically attributed.</para>
/// <para><strong>All-or-nothing.</strong> The step does not partially execute
/// a batch. As soon as any action denies, the entire request is aborted with
/// a single <see cref="AccessDeniedException"/>. Rationale: simpler model,
/// clearer error messages, and a far smaller blast radius for misconfigured
/// rules. Operators wanting per-statement isolation should issue separate
/// requests.</para>
/// <para><strong>Profile-less contexts.</strong> Core unit tests sometimes
/// run the pipeline without bootstrapping the profile accessor. When
/// <see cref="QueryExecutionContext.Profile"/> is null the step falls back to
/// <see cref="ProfileId.Default"/> so the bootstrapping bypass still applies
/// and the test does not have to wire up an evaluator stub.</para>
/// </remarks>
public sealed class AclQueryStep : IQueryExecutionStep
{
    private readonly IAclEvaluator _evaluator;

    /// <summary>Creates a new <see cref="AclQueryStep"/>.</summary>
    public AclQueryStep(IAclEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(evaluator);
        _evaluator = evaluator;
    }

    /// <inheritdoc />
    public int Order => QueryStepOrder.Authorization;

    /// <inheritdoc />
    public async Task ExecuteAsync(QueryExecutionContext context, QueryStepDelegate next, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var parsed = context.Parsed
            ?? throw new InvalidOperationException(
                "AclQueryStep requires the parse step to have run first.");

        var profile = context.Profile?.Id ?? ProfileId.Default;
        var descriptor = context.Connection.Descriptor;

        for (var s = 0; s < parsed.Statements.Count; s++)
        {
            var statement = parsed.Statements[s];
            for (var a = 0; a < statement.Actions.Count; a++)
            {
                var action = statement.Actions[a];
                var operation = MapOperation(action.Action);
                if (operation == AclOperation.None)
                {
                    continue;
                }

                // Table-level check — column is null.
                var tableRequest = new AclEvaluationRequest(
                    profile,
                    descriptor,
                    action.Target,
                    operation);
                var tableDecision = await _evaluator.EvaluateAsync(tableRequest, cancellationToken).ConfigureAwait(false);
                if (!tableDecision.IsAllowed)
                {
                    throw Build(profile, operation, action.Target, column: null, tableDecision.Reason);
                }

                // Column-level checks — only for columns whose usage is meaningful for ACL
                // and whose reference is resolvable to the action's table.
                for (var c = 0; c < action.Columns.Count; c++)
                {
                    var column = action.Columns[c];
                    if (!ShouldCheckColumn(column))
                    {
                        continue;
                    }

                    if (!ColumnBelongsToActionTarget(column, action.Target))
                    {
                        continue;
                    }

                    var columnOperation = MapColumnOperation(column.Usage, action.Action);
                    if (columnOperation == AclOperation.None)
                    {
                        continue;
                    }

                    var columnRequest = new AclEvaluationRequest(
                        profile,
                        descriptor,
                        action.Target,
                        columnOperation,
                        column.Name);
                    var columnDecision = await _evaluator.EvaluateAsync(columnRequest, cancellationToken).ConfigureAwait(false);
                    if (!columnDecision.IsAllowed)
                    {
                        throw Build(profile, columnOperation, action.Target, column.Name, columnDecision.Reason);
                    }
                }
            }
        }

        await next(cancellationToken).ConfigureAwait(false);
    }

    private static AclOperation MapOperation(ActionKind action) => action switch
    {
        ActionKind.Read => AclOperation.Read,
        ActionKind.Insert => AclOperation.Insert,
        ActionKind.Update => AclOperation.Update,
        ActionKind.Delete => AclOperation.Delete,
        ActionKind.Truncate => AclOperation.Truncate,
        ActionKind.Create => AclOperation.Create,
        ActionKind.Alter => AclOperation.Alter,
        ActionKind.Drop => AclOperation.Drop,
        ActionKind.Execute => AclOperation.Execute,
        ActionKind.Grant => AclOperation.Grant,
        ActionKind.Revoke => AclOperation.Revoke,
        _ => AclOperation.None,
    };

    private static AclOperation MapColumnOperation(ColumnUsage usage, ActionKind action) => usage switch
    {
        ColumnUsage.Projected => AclOperation.Read,
        ColumnUsage.Filtered => AclOperation.Read,
        ColumnUsage.Joined => AclOperation.Read,
        ColumnUsage.OrderedBy => AclOperation.Read,
        ColumnUsage.GroupedBy => AclOperation.Read,
        ColumnUsage.Inserted => AclOperation.Insert,
        ColumnUsage.Modified => MapOperation(action),
        _ => AclOperation.None,
    };

    private static bool ShouldCheckColumn(ColumnReference column)
    {
        // Skip the synthetic "*" projected expansion produced by `SELECT *`
        // and any column whose name is empty.
        if (string.IsNullOrEmpty(column.Name) || column.Name == "*")
        {
            return false;
        }

        return true;
    }

    private static bool ColumnBelongsToActionTarget(ColumnReference column, ObjectReference target)
    {
        // When the parser produced an explicit table reference for the column,
        // require it to match the action target. When it produced a bare name
        // (cannot statically resolve), trust the parser's attribution since it
        // already bound the column to this action.
        if (column.Table is null)
        {
            return true;
        }

        if (!string.Equals(column.Table, target.Name, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (column.Schema is not null
            && target.Schema is not null
            && !string.Equals(column.Schema, target.Schema, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static AccessDeniedException Build(ProfileId profile, AclOperation operation, ObjectReference target, string? column, string reason)
    {
        var scope = column is null
            ? target.QualifiedName
            : $"{target.QualifiedName}.{column}";
        return new AccessDeniedException(profile, operation, scope, reason);
    }
}
