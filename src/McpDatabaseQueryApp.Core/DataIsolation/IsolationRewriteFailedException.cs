namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Thrown by <see cref="IsolationRuleStep"/> when the rewriter rejects a
/// mandatory isolation directive. The pipeline fails closed: refusing the
/// query is preferable to running it without the operator-mandated filter.
/// </summary>
public sealed class IsolationRewriteFailedException : Exception
{
    /// <summary>
    /// Creates a new <see cref="IsolationRewriteFailedException"/>.
    /// </summary>
    public IsolationRewriteFailedException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates a new <see cref="IsolationRewriteFailedException"/> with an
    /// inner exception (typically <see cref="QueryParsing.QueryParseException"/>).
    /// </summary>
    public IsolationRewriteFailedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
