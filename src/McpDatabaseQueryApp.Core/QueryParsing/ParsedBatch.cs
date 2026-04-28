namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Result of parsing a SQL batch (zero or more statements separated by the
/// dialect's batch terminator). The original text is preserved verbatim so
/// rewriters can splice modifications by offset.
/// </summary>
public sealed record ParsedBatch
{
    public ParsedBatch(
        DatabaseKind dialect,
        string originalSql,
        IReadOnlyList<ParsedStatement> statements,
        IReadOnlyList<ParseDiagnostic> errors,
        ProviderAst providerAst)
    {
        ArgumentNullException.ThrowIfNull(originalSql);
        ArgumentNullException.ThrowIfNull(statements);
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(providerAst);

        Dialect = dialect;
        OriginalSql = originalSql;
        Statements = statements;
        Errors = errors;
        ProviderAst = providerAst;
    }

    public DatabaseKind Dialect { get; init; }

    public string OriginalSql { get; init; }

    public IReadOnlyList<ParsedStatement> Statements { get; init; }

    /// <summary>
    /// Recoverable parse errors. Hard syntax failures bubble up as a
    /// <see cref="QueryParseException"/> instead of populating this list.
    /// </summary>
    public IReadOnlyList<ParseDiagnostic> Errors { get; init; }

    /// <summary>
    /// Whole-batch provider AST handle. Per-statement handles are also
    /// available on each <see cref="ParsedStatement.ProviderAst"/>.
    /// </summary>
    public ProviderAst ProviderAst { get; init; }

    public bool HasErrors => Errors.Count > 0;

    public bool ContainsMutation
    {
        get
        {
            for (var i = 0; i < Statements.Count; i++)
            {
                if (Statements[i].IsMutation)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public bool ContainsDestructive
    {
        get
        {
            for (var i = 0; i < Statements.Count; i++)
            {
                if (Statements[i].IsDestructive)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Convenience accessor that downcasts the provider AST to
    /// <typeparamref name="T"/>. Returns <c>null</c> when the AST tag does
    /// not match.
    /// </summary>
    public T? GetProviderState<T>() where T : class
        => ProviderAst.As<T>();
}
