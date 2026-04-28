using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Default <see cref="IQueryClassifier"/>. Delegates to the
/// <see cref="IQueryParserFactory"/> registered in DI and converts the
/// resulting <see cref="ParsedBatch"/> into a flat
/// <see cref="QueryClassification"/>.
/// </summary>
public sealed class QueryClassifier : IQueryClassifier
{
    private readonly IQueryParserFactory _parsers;

    public QueryClassifier(IQueryParserFactory parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);
        _parsers = parsers;
    }

    /// <inheritdoc />
    public QueryClassification Classify(DatabaseKind kind, string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return QueryClassification.Empty;
        }

        var parser = _parsers.GetParser(kind);
        ParsedBatch parsed;
        try
        {
            parsed = parser.Parse(sql);
        }
        catch (QueryParseException ex)
        {
            throw new QuerySyntaxException(kind, $"Failed to parse {kind} SQL: {ex.Message}", ex);
        }

        var destructiveList = new List<DestructiveStatement>(parsed.Statements.Count);
        var containsMutation = false;
        var containsDestructive = false;

        for (var i = 0; i < parsed.Statements.Count; i++)
        {
            var stmt = parsed.Statements[i];
            if (stmt.IsMutation)
            {
                containsMutation = true;
            }

            if (stmt.IsDestructive)
            {
                containsDestructive = true;
                destructiveList.Add(new DestructiveStatement(
                    stmt.StatementKind,
                    DestructiveReasonFormatter.Format(stmt),
                    stmt.OriginalText));
            }
        }

        return new QueryClassification(containsMutation, containsDestructive, destructiveList);
    }
}
