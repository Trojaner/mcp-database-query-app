namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// A single SQL statement extracted from a batch. Immutable — every
/// collection is exposed as <see cref="IReadOnlyList{T}"/>.
/// </summary>
public sealed record ParsedStatement
{
    public ParsedStatement(
        StatementKind kind,
        bool isMutation,
        bool isDestructive,
        string originalText,
        SourceRange range,
        IReadOnlyList<ParsedQueryAction> actions,
        IReadOnlyList<ParseDiagnostic> warnings,
        ProviderAst providerAst)
    {
        ArgumentNullException.ThrowIfNull(originalText);
        ArgumentNullException.ThrowIfNull(actions);
        ArgumentNullException.ThrowIfNull(warnings);
        ArgumentNullException.ThrowIfNull(providerAst);

        StatementKind = kind;
        IsMutation = isMutation;
        IsDestructive = isDestructive;
        OriginalText = originalText;
        Range = range;
        Actions = actions;
        Warnings = warnings;
        ProviderAst = providerAst;
    }

    public StatementKind StatementKind { get; init; }

    /// <summary>
    /// True for INSERT/UPDATE/DELETE/MERGE/TRUNCATE and DDL statements that
    /// alter persistent state. Pure SELECT and SET-style transient
    /// statements are not mutations.
    /// </summary>
    public bool IsMutation { get; init; }

    /// <summary>
    /// True for irreversible DDL (DROP, TRUNCATE) and unbounded DML
    /// (DELETE/UPDATE without a WHERE clause). Flagged by elicitation guards.
    /// </summary>
    public bool IsDestructive { get; init; }

    public string OriginalText { get; init; }

    public SourceRange Range { get; init; }

    public IReadOnlyList<ParsedQueryAction> Actions { get; init; }

    public IReadOnlyList<ParseDiagnostic> Warnings { get; init; }

    /// <summary>
    /// Provider-specific AST handle. Opaque to Core consumers; downcast
    /// inside the producing provider via <see cref="ProviderAst.As{T}"/>.
    /// </summary>
    public ProviderAst ProviderAst { get; init; }
}
