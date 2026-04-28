namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Strongly-typed identifier for a data-isolation rule. Wraps a non-empty
/// string value; the wrapped value is opaque to callers and may be a GUID,
/// a config-derived stable id, or any other unique token.
/// </summary>
public readonly record struct IsolationRuleId
{
    /// <summary>The raw rule id value.</summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new <see cref="IsolationRuleId"/>. Throws if
    /// <paramref name="value"/> is null or whitespace.
    /// </summary>
    public IsolationRuleId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Isolation rule id must not be null or whitespace.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Generates a fresh rule id backed by a GUID.
    /// </summary>
    public static IsolationRuleId NewId() => new(Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public override string ToString() => Value;
}
