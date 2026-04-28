using System.Text;
using McpDatabaseQueryApp.Core.QueryExecution;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Elicitation;

/// <summary>
/// MCP-bound implementation of <see cref="IDestructiveOperationConfirmer"/>.
/// Reads the current <see cref="IMcpServer"/> off
/// <see cref="QueryExecutionContext.Items"/> at the entry-point key
/// <see cref="ContextKey"/> (set by the calling tool), then drives the
/// existing <see cref="IElicitationGateway"/> form prompt.
/// </summary>
/// <remarks>
/// Passing the server through <c>Items</c> avoids needing a per-request
/// <see cref="AsyncLocal{T}"/> accessor while still keeping Core free of the
/// MCP SDK. <see cref="QueryExecutionPipelineRunner"/> wraps the assignment
/// so individual tools never touch the key directly.
/// </remarks>
public sealed class McpDestructiveOperationConfirmer : IDestructiveOperationConfirmer
{
    /// <summary>Key used to stash the current <see cref="IMcpServer"/> on the context.</summary>
    public const string ContextKey = "McpServer";

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
            || raw is not McpServer server)
        {
            return null;
        }

        if (!_elicitation.ClientSupportsForm(server))
        {
            return null;
        }

        var message = BuildMessage(statements);
        var ok = await _elicitation.ConfirmAsync(server, message, cancellationToken).ConfigureAwait(false);
        return ok;
    }

    private static string BuildMessage(IReadOnlyList<DestructiveStatement> statements)
    {
        var sb = new StringBuilder();
        sb.Append("This batch contains ").Append(statements.Count).AppendLine(" destructive statement(s):");
        sb.AppendLine();
        for (var i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            sb.Append(i + 1).Append(". [").Append(s.Kind).Append("] ").AppendLine(s.Reason);
            sb.AppendLine(s.Sql);
            sb.AppendLine();
        }

        sb.Append("Proceed?");
        return sb.ToString();
    }
}
