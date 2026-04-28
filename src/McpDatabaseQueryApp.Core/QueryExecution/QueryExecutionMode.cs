namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Indicates how the SQL associated with a <see cref="QueryExecutionContext"/>
/// is intended to be executed. The pipeline uses this to differentiate the
/// read tool surface (db_query) from the write/explain surfaces.
/// </summary>
public enum QueryExecutionMode
{
    /// <summary>
    /// Read-only execution path (e.g. <c>db_query</c>). Mutations are rejected
    /// outright, regardless of the connection's read-only flag.
    /// </summary>
    Read = 0,

    /// <summary>
    /// Write execution path (e.g. <c>db_execute</c>, script runs). Mutations
    /// are allowed unless the connection itself is read-only; destructive
    /// operations require confirmation.
    /// </summary>
    Write = 1,

    /// <summary>
    /// Explain execution path (e.g. <c>db_explain</c>). The pipeline parses
    /// and validates the SQL but allows DML statements because most engines
    /// accept <c>EXPLAIN</c> over any DML without performing the side effect.
    /// </summary>
    Explain = 2,
}
