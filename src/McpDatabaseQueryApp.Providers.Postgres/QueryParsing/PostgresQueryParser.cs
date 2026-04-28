using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryParsing;
using PgSqlParser;

namespace McpDatabaseQueryApp.Providers.Postgres.QueryParsing;

/// <summary>
/// PostgreSQL parser backed by libpg_query (via the
/// <c>pgsqlparser</c> .NET binding). Stateless and thread-safe — registered
/// as a singleton.
/// </summary>
public sealed class PostgresQueryParser : IQueryParser
{
    /// <inheritdoc />
    public DatabaseKind Kind => DatabaseKind.Postgres;

    /// <inheritdoc />
    public ParsedBatch Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var result = Parser.Parse(sql, ParserOptions.Default);
        if (!result.IsSuccess || result.Value is null)
        {
            var msg = result.Error?.Message ?? "Unknown libpg_query parse failure.";
            var range = new SourceRange(result.Error?.CursorPos ?? 0, 0);
            throw new QueryParseException(
                $"Failed to parse SQL: {msg}",
                [new ParseDiagnostic(ParseSeverity.Error, msg, range)]);
        }

        var parseResult = result.Value;

        var statements = new List<ParsedStatement>(parseResult.Stmts.Count);
        for (var i = 0; i < parseResult.Stmts.Count; i++)
        {
            var raw = parseResult.Stmts[i];
            statements.Add(BuildStatement(raw, sql, parseResult));
        }

        var batchAst = new ProviderAst(new PgQueryAstHandle(parseResult, RawStmt: null))
        {
            Tag = PgQueryAstHandle.TagName,
        };

        return new ParsedBatch(
            DatabaseKind.Postgres,
            sql,
            statements,
            errors: [],
            batchAst);
    }

    private static ParsedStatement BuildStatement(RawStmt raw, string originalSql, ParseResult parseResult)
    {
        var (kind, isMutation, isDestructive) = PgStatementClassifier.Classify(raw.Stmt);

        var extractor = new PgActionExtractor();
        var (actions, _) = extractor.Extract(raw.Stmt);

        var (range, originalText) = ResolveRange(raw, originalSql);

        return new ParsedStatement(
            kind,
            isMutation,
            isDestructive,
            originalText,
            range,
            actions,
            warnings: [],
            providerAst: new ProviderAst(new PgQueryAstHandle(parseResult, raw))
            {
                Tag = PgQueryAstHandle.TagName,
            });
    }

    /// <summary>
    /// libpg_query encodes <c>StmtLen == 0</c> as "the rest of the input".
    /// Normalise that into an explicit byte range and slice the original
    /// SQL text.
    /// </summary>
    private static (SourceRange Range, string Text) ResolveRange(RawStmt raw, string sql)
    {
        var start = Math.Max(0, raw.StmtLocation);
        if (start > sql.Length)
        {
            start = sql.Length;
        }

        int len;
        if (raw.StmtLen <= 0)
        {
            len = sql.Length - start;
        }
        else
        {
            len = Math.Min(raw.StmtLen, sql.Length - start);
        }
        if (len < 0)
        {
            len = 0;
        }

        var text = sql.Substring(start, len);
        return (new SourceRange(start, len), text);
    }
}
