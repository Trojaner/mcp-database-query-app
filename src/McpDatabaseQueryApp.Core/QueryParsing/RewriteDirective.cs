namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Marker base type for instructions consumed by an
/// <see cref="IQueryRewriter"/>. Concrete directives are sealed records so
/// rewriters can pattern-match exhaustively.
/// </summary>
public abstract record RewriteDirective;

/// <summary>
/// Inject an additional predicate into the WHERE clause of every statement
/// targeting <see cref="Target"/>. The predicate is appended as
/// <c>AND (predicate)</c>; if the statement has no WHERE clause, one is
/// added.
/// </summary>
/// <param name="Target">Table the predicate is bound to. Comparison is
/// case-insensitive on identifiers and ignores parts that the directive
/// leaves <c>null</c> (e.g. omitting <c>Schema</c> matches any schema).</param>
/// <param name="Predicate">Raw SQL predicate body — must be a complete
/// boolean expression. The rewriter wraps it in parentheses, so callers do
/// not need to.</param>
public sealed record PredicateInjectionDirective(
    ObjectReference Target,
    string Predicate) : RewriteDirective;
