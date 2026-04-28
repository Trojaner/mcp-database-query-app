namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Rewrites a previously-parsed batch by applying a list of directives.
/// One implementation per <see cref="DatabaseKind"/>, registered alongside
/// the matching <see cref="IQueryParser"/>.
/// </summary>
public interface IQueryRewriter
{
    DatabaseKind Kind { get; }

    /// <summary>
    /// Returns a SQL string equivalent to the original batch with
    /// <paramref name="directives"/> applied. The output is round-trippable
    /// — feeding it back through <see cref="IQueryParser.Parse"/> yields a
    /// semantically equivalent batch.
    /// </summary>
    /// <exception cref="QueryParseException">
    /// Thrown when the rewriter cannot honor a directive (for example when
    /// the parsed batch carries no provider AST, or a directive targets a
    /// statement shape the rewriter does not support).
    /// </exception>
    string Rewrite(ParsedBatch parsed, IReadOnlyList<RewriteDirective> directives);
}
