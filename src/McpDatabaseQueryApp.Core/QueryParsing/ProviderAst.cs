namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Opaque wrapper around a provider-specific AST handle. Core code treats
/// the contained value as a black box; provider assemblies downcast via
/// <see cref="As{T}"/> from inside their own assembly. The wrapper is
/// sealed and immutable so the AST cannot be swapped after the parser
/// produces it.
/// </summary>
/// <remarks>
/// Stuffing the AST inside <see cref="ParsedBatch"/> via this wrapper is
/// approach #1 from the design brief: parsers attach their native parse
/// tree at parse time, and the matching <see cref="IQueryRewriter"/>
/// retrieves it without leaking dialect-specific types into the Core API.
/// </remarks>
public sealed class ProviderAst
{
    private readonly object? _value;

    public ProviderAst(object? value)
    {
        _value = value;
    }

    /// <summary>
    /// Identifier the parser used to tag the AST flavor (e.g. "tsql",
    /// "pgquery"). Useful for assertions / sanity checks.
    /// </summary>
    public string? Tag { get; init; }

    /// <summary>
    /// Returns the raw value without a cast. Prefer <see cref="As{T}"/>.
    /// </summary>
    public object? Value => _value;

    /// <summary>
    /// Returns the AST cast to <typeparamref name="T"/>, or <c>null</c> if
    /// the contents do not match. Providers should accept the resulting
    /// <c>null</c> as "not my AST" and bail out of the rewrite.
    /// </summary>
    public T? As<T>() where T : class
        => _value as T;

    public static readonly ProviderAst None = new(null);
}
