using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Thrown when the parse step cannot produce an AST for the supplied SQL.
/// Wraps the upstream <see cref="QueryParseException"/> with a stable,
/// dialect-aware message suitable for tool error reporting.
/// </summary>
public sealed class QuerySyntaxException : Exception
{
    public QuerySyntaxException(DatabaseKind dialect, string message)
        : base(message)
    {
        Dialect = dialect;
    }

    public QuerySyntaxException(DatabaseKind dialect, string message, Exception innerException)
        : base(message, innerException)
    {
        Dialect = dialect;
    }

    /// <summary>The dialect whose parser rejected the SQL.</summary>
    public DatabaseKind Dialect { get; }
}

/// <summary>
/// Thrown when the SQL contains a mutation but the underlying connection is
/// flagged as read-only.
/// </summary>
public sealed class ReadOnlyConnectionViolationException : Exception
{
    public ReadOnlyConnectionViolationException(string message, IReadOnlyList<string> offendingStatements)
        : base(message)
    {
        OffendingStatements = offendingStatements;
    }

    /// <summary>Truncated text of the statements that caused the violation.</summary>
    public IReadOnlyList<string> OffendingStatements { get; }
}

/// <summary>
/// Thrown when a mutation appears on the read-only tool surface
/// (<c>db_query</c>). Mutations belong on <c>db_execute</c>.
/// </summary>
public sealed class MutationOnReadPathException : Exception
{
    public MutationOnReadPathException(string message, IReadOnlyList<string> offendingStatements)
        : base(message)
    {
        OffendingStatements = offendingStatements;
    }

    public IReadOnlyList<string> OffendingStatements { get; }
}

/// <summary>
/// Thrown when destructive SQL is submitted by a client that does not support
/// elicitation, leaving no channel to obtain an explicit confirmation.
/// </summary>
public sealed class DestructiveOperationConfirmationRequiredException : Exception
{
    public DestructiveOperationConfirmationRequiredException(
        string message,
        IReadOnlyList<DestructiveStatement> statements)
        : base(message)
    {
        Statements = statements;
    }

    public IReadOnlyList<DestructiveStatement> Statements { get; }
}

/// <summary>
/// Thrown when the user explicitly declines the destructive-operation
/// confirmation. Surfaces as a clean tool error rather than a generic
/// failure.
/// </summary>
public sealed class DestructiveOperationCancelledException : Exception
{
    public DestructiveOperationCancelledException(
        string message,
        IReadOnlyList<DestructiveStatement> statements)
        : base(message)
    {
        Statements = statements;
    }

    public IReadOnlyList<DestructiveStatement> Statements { get; }
}
