using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;

/// <summary>
/// T-SQL parser backed by ScriptDom's <c>TSql160Parser</c> (SQL Server 2022).
/// Stateless and thread-safe — registered as a singleton.
/// </summary>
public sealed class SqlServerQueryParser : IQueryParser
{
    public DatabaseKind Kind => DatabaseKind.SqlServer;

    public ParsedBatch Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var parser = new TSql160Parser(initialQuotedIdentifiers: true);
        TSqlFragment fragment;
        IList<ParseError>? scriptDomErrors;

        using (var reader = new StringReader(sql))
        {
            fragment = parser.Parse(reader, out scriptDomErrors);
        }

        var errors = new List<ParseDiagnostic>(scriptDomErrors?.Count ?? 0);
        if (scriptDomErrors is not null)
        {
            foreach (var err in scriptDomErrors)
            {
                errors.Add(new ParseDiagnostic(
                    ParseSeverity.Error,
                    err.Message,
                    new SourceRange(err.Offset, 0),
                    err.Number.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        var script = fragment as TSqlScript;
        var statements = new List<ParsedStatement>();
        if (script is not null)
        {
            foreach (var batch in script.Batches)
            {
                foreach (var statement in batch.Statements)
                {
                    statements.Add(BuildStatement(statement, sql));
                }
            }
        }
        else
        {
            // ScriptDom returned a fragment but not a TSqlScript — surface
            // it as a single Other statement so callers still see something.
            statements.Add(new ParsedStatement(
                StatementKind.Other,
                isMutation: false,
                isDestructive: false,
                originalText: ExtractOriginalText(fragment, sql),
                range: ToRange(fragment),
                actions: [],
                warnings: [],
                providerAst: new ProviderAst(new TSqlAstHandle(fragment, null)) { Tag = TSqlAstHandle.TagName }));
        }

        var batchAst = new ProviderAst(new TSqlAstHandle(fragment, script))
        {
            Tag = TSqlAstHandle.TagName,
        };

        return new ParsedBatch(
            DatabaseKind.SqlServer,
            sql,
            statements,
            errors,
            batchAst);
    }

    private static ParsedStatement BuildStatement(TSqlStatement statement, string originalSql)
    {
        var (kind, isMutation, isDestructive) = TSqlStatementClassifier.Classify(statement);

        var extractor = new TSqlActionExtractor();
        var (actions, _) = extractor.Extract(statement);

        return new ParsedStatement(
            kind,
            isMutation,
            isDestructive,
            originalText: ExtractOriginalText(statement, originalSql),
            range: ToRange(statement),
            actions: actions,
            warnings: [],
            providerAst: new ProviderAst(new TSqlAstHandle(statement, null))
            {
                Tag = TSqlAstHandle.TagName,
            });
    }

    private static SourceRange ToRange(TSqlFragment fragment)
        => new(fragment.StartOffset, fragment.FragmentLength);

    private static string ExtractOriginalText(TSqlFragment fragment, string sql)
    {
        var start = Math.Max(0, fragment.StartOffset);
        var len = Math.Max(0, Math.Min(fragment.FragmentLength, sql.Length - start));
        return sql.Substring(start, len);
    }
}
