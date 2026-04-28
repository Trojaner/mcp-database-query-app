namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Strongly-typed identifier for an <see cref="AclEntry"/>. Wraps a non-empty
/// string value so call sites cannot accidentally interchange ACL ids with
/// other ids that happen to be strings.
/// </summary>
public readonly record struct AclEntryId
{
    /// <summary>
    /// Creates a new <see cref="AclEntryId"/>. Throws if <paramref name="value"/>
    /// is null or whitespace.
    /// </summary>
    public AclEntryId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("ACL entry id must not be null or whitespace.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The raw id value.</summary>
    public string Value { get; }

    /// <summary>
    /// Generates a fresh, opaque ACL entry id suitable for new rows. Uses a
    /// GUID rendered as a 32-character hex string.
    /// </summary>
    public static AclEntryId NewId() => new("acl_" + Guid.NewGuid().ToString("N"));

    /// <inheritdoc/>
    public override string ToString() => Value;
}
