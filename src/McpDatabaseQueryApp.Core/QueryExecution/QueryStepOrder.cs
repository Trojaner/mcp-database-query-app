namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Canonical ordering values for built-in pipeline steps. Steps with smaller
/// values run earlier. Values are spaced by 100 so future steps can be
/// inserted between existing ones without renumbering.
/// </summary>
/// <remarks>
/// <para>
/// Slots <see cref="Authorization"/> (200) and <see cref="Rewriting"/> (300)
/// are reserved for ACL enforcement (Task 4) and connection-aware SQL
/// rewriting (Task 5). Do not register steps at those values from this task.
/// </para>
/// </remarks>
public static class QueryStepOrder
{
    /// <summary>Parses the SQL into a <see cref="QueryParsing.ParsedBatch"/>.</summary>
    public const int Parse = 100;

    /// <summary>Reserved for the ACL/authorization step (Task 4).</summary>
    public const int Authorization = 200;

    /// <summary>Reserved for the SQL rewriter step (Task 5).</summary>
    public const int Rewriting = 300;

    /// <summary>Enforces read-only and destructive-operation policies.</summary>
    public const int Safety = 400;

    /// <summary>Emits structured logging for the query before execution.</summary>
    public const int Logging = 500;
}
