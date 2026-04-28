using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// AST-driven SQL classification helper for callers that need a quick
/// safety verdict outside the full <see cref="IQueryPipeline"/> (e.g. UI hint
/// generation, script-record bootstrapping).
/// </summary>
public interface IQueryClassifier
{
    /// <summary>
    /// Classifies <paramref name="sql"/> against the parser registered for
    /// <paramref name="kind"/>. Returns an empty classification if the SQL
    /// is whitespace; throws <see cref="QuerySyntaxException"/> if the SQL
    /// cannot be parsed.
    /// </summary>
    QueryClassification Classify(DatabaseKind kind, string sql);
}

/// <summary>
/// Result of a single classification call.
/// </summary>
/// <param name="ContainsMutation">True if any statement in the batch mutates persistent state.</param>
/// <param name="ContainsDestructive">True if any statement is irreversible or unbounded.</param>
/// <param name="DestructiveStatements">Per-statement detail for prompts and logs.</param>
public sealed record QueryClassification(
    bool ContainsMutation,
    bool ContainsDestructive,
    IReadOnlyList<DestructiveStatement> DestructiveStatements)
{
    /// <summary>Empty classification used for blank input.</summary>
    public static QueryClassification Empty { get; } = new(false, false, []);
}
