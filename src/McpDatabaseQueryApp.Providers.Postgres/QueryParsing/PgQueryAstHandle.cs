using McpDatabaseQueryApp.Core.QueryParsing;
using PgSqlParser;

namespace McpDatabaseQueryApp.Providers.Postgres.QueryParsing;

/// <summary>
/// Provider AST payload for the Postgres parser. Wraps the libpg_query
/// protobuf <see cref="ParseResult"/> for the entire batch and, when
/// applicable, the individual <see cref="RawStmt"/> a particular
/// <see cref="ParsedStatement"/> was produced from. The rewriter mutates
/// the AST in place and re-emits SQL via <see cref="Parser.Deparse"/>.
/// </summary>
internal sealed record PgQueryAstHandle(ParseResult ParseResult, RawStmt? RawStmt)
{
    /// <summary>
    /// Tag value stored on <see cref="ProviderAst.Tag"/> for Postgres ASTs.
    /// </summary>
    public const string TagName = "pgquery";
}
