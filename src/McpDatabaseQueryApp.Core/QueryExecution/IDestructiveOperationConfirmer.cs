namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Abstracts the destructive-operation confirmation channel. The Core
/// project owns the contract; the Server project supplies the MCP-specific
/// elicitation-backed implementation.
/// </summary>
public interface IDestructiveOperationConfirmer
{
    /// <summary>
    /// Asks the user to confirm the destructive statements.
    /// </summary>
    /// <returns>
    /// <c>true</c> when the user explicitly accepted, <c>false</c> when they
    /// declined, and <c>null</c> when no confirmation channel is available
    /// (the client does not support elicitation).
    /// </returns>
    Task<bool?> ConfirmAsync(IReadOnlyList<DestructiveStatement> statements, CancellationToken cancellationToken);
}
