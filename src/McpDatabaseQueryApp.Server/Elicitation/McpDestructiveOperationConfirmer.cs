using System.Text;
using McpDatabaseQueryApp.Core.QueryExecution;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Elicitation;

/// <summary>
/// MCP-bound implementation of <see cref="IDestructiveOperationConfirmer"/>.
/// Reads the current tool <see cref="RequestContext{TParams}"/> off
/// <see cref="QueryExecutionContext.Items"/> at the entry-point key
/// <see cref="ContextKey"/> (set by the calling tool), then drives the
/// <see cref="IElicitationGateway"/> form prompt.
/// </summary>
/// <remarks>
/// Passing the request context through <c>Items</c> avoids needing a per-request
/// <see cref="AsyncLocal{T}"/> accessor while still keeping Core free of the
/// MCP SDK. The full request context (rather than just the <see cref="McpServer"/>)
/// is required because the multi round-trip request pattern reads the user's answer
/// from <c>Params.InputResponses</c> on the replayed call.
/// </remarks>
public sealed class McpDestructiveOperationConfirmer : IDestructiveOperationConfirmer
{
    /// <summary>Key used to stash the current tool request context on the pipeline context.</summary>
    public const string ContextKey = "McpRequestContext";

    /// <summary>MRTR input key for the destructive-batch confirmation prompt.</summary>
    public const string InputKey = "confirm_destructive";

    private readonly IElicitationGateway _elicitation;
    private readonly IQueryExecutionContextAccessor _accessor;

    public McpDestructiveOperationConfirmer(
        IElicitationGateway elicitation,
        IQueryExecutionContextAccessor accessor)
    {
        ArgumentNullException.ThrowIfNull(elicitation);
        ArgumentNullException.ThrowIfNull(accessor);
        _elicitation = elicitation;
        _accessor = accessor;
    }

    /// <inheritdoc />
    public async Task<bool?> ConfirmAsync(IReadOnlyList<DestructiveStatement> statements, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statements);

        var context = _accessor.Current;
        if (context is null
            || !context.Items.TryGetValue(ContextKey, out var raw)
            || raw is not RequestContext<CallToolRequestParams> requestContext)
        {
            return null;
        }

        if (!_elicitation.CanElicit(requestContext))
        {
            return null;
        }

        var message = BuildMessage(statements);
        var ok = await _elicitation.ConfirmAsync(requestContext, InputKey, message, cancellationToken).ConfigureAwait(false);
        return ok;
    }

    private static string BuildMessage(IReadOnlyList<DestructiveStatement> statements)
    {
        var destructiveCount = 0;
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i].IsDestructive)
            {
                destructiveCount++;
            }
        }

        var sb = new StringBuilder();
        sb.Append("This batch contains ").Append(statements.Count).Append(" statement(s) that change the database");
        if (destructiveCount > 0)
        {
            sb.Append(", ").Append(destructiveCount).Append(" of them destructive");
        }

        sb.AppendLine(":");
        sb.AppendLine();
        for (var i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            sb.Append(i + 1).Append(". [").Append(s.Kind);
            if (s.IsDestructive)
            {
                sb.Append(" — DESTRUCTIVE");
            }

            sb.Append("] ").AppendLine(s.Reason);
            sb.AppendLine(s.Sql);
            sb.AppendLine();
        }

        sb.Append("Proceed?");
        return sb.ToString();
    }
}
