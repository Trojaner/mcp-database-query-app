using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Mutable per-invocation state carried through the
/// <see cref="IQueryPipeline"/>. Steps may rewrite <see cref="Sql"/>, attach
/// the parse result to <see cref="Parsed"/>, or stash arbitrary entries on
/// <see cref="Items"/> to communicate with later steps.
/// </summary>
/// <remarks>
/// <para>
/// The context is constructed once per pipeline invocation by the calling
/// tool and disposed implicitly when the call returns. Steps must not retain
/// references to it across pipeline boundaries.
/// </para>
/// <para>
/// <see cref="Profile"/> may be <c>null</c> in low-level Core unit tests that
/// run the pipeline without bootstrapping the profile accessor.
/// </para>
/// </remarks>
public sealed class QueryExecutionContext
{
    /// <summary>
    /// Creates a new context. <paramref name="connection"/> exposes the
    /// underlying database; the pipeline never executes on it directly —
    /// callers run the SQL after the pipeline returns.
    /// </summary>
    public QueryExecutionContext(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        IDatabaseConnection connection,
        QueryExecutionMode mode,
        bool confirmDestructive,
        bool confirmUnlimited,
        Profile? profile = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(connection);

        Sql = sql;
        Parameters = parameters;
        Connection = connection;
        ConnectionId = connection.Id;
        Kind = connection.Kind;
        Mode = mode;
        ConfirmDestructive = confirmDestructive;
        ConfirmUnlimited = confirmUnlimited;
        Profile = profile;
        Items = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// SQL associated with the request. Rewriting steps (Task 5) may replace
    /// this value before downstream steps observe it.
    /// </summary>
    public string Sql { get; set; }

    /// <summary>
    /// Bound parameters supplied alongside <see cref="Sql"/>, or
    /// <c>null</c> if none.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; set; }

    /// <summary>
    /// AST representation populated by the parse step. <c>null</c> until
    /// <see cref="Steps.ParseQueryStep"/> runs successfully.
    /// </summary>
    public ParsedBatch? Parsed { get; set; }

    /// <summary>The connection id the query targets.</summary>
    public string ConnectionId { get; }

    /// <summary>The (read-only) handle to the live connection.</summary>
    public IDatabaseConnection Connection { get; }

    /// <summary>Dialect of <see cref="Connection"/>.</summary>
    public DatabaseKind Kind { get; }

    /// <summary>
    /// Ambient profile under which the pipeline runs. May be <c>null</c> when
    /// invoked outside the request scope (Core unit tests).
    /// </summary>
    public Profile? Profile { get; set; }

    /// <summary>Read/write/explain semantics.</summary>
    public QueryExecutionMode Mode { get; }

    /// <summary>
    /// Caller's <c>confirm</c> flag. Lets a trusted caller (when the server
    /// is started with <c>--dangerously-skip-permissions</c>) bypass the
    /// destructive-operation elicitation.
    /// </summary>
    public bool ConfirmDestructive { get; }

    /// <summary>
    /// Caller's <c>confirmUnlimited</c> flag for the row-limit guard.
    /// Currently consulted outside the pipeline by the result limiter; kept
    /// here so future steps can co-locate the policy.
    /// </summary>
    public bool ConfirmUnlimited { get; }

    /// <summary>
    /// Free-form per-invocation bag for step-to-step extensibility, modelled
    /// after <c>HttpContext.Items</c>. Use a stable string key.
    /// </summary>
    public IDictionary<string, object?> Items { get; }
}
