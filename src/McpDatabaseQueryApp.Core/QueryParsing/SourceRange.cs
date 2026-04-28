namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Inclusive-exclusive offset range into the original SQL text for a parsed
/// element. <see cref="Length"/> may exceed the remaining text when the
/// underlying parser approximates locations; rewriters MUST clamp before
/// indexing.
/// </summary>
public readonly record struct SourceRange(int Start, int Length)
{
    public int End => Start + Length;

    public bool IsEmpty => Length == 0;

    public static readonly SourceRange Empty = new(0, 0);
}
