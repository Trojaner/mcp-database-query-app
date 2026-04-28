namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Strongly-typed identifier for a profile. A profile partitions all
/// per-tenant state (connections, databases, scripts, notes, result sets)
/// in the metadata store, and identifies which key is used to derive the
/// AEAD master key for encrypting credentials at rest.
/// </summary>
/// <remarks>
/// Wraps a non-empty string value. The reserved value <c>default</c> denotes
/// the built-in fallback profile used when no OAuth2 identity is present.
/// </remarks>
public readonly record struct ProfileId
{
    /// <summary>
    /// The reserved identifier of the built-in default profile.
    /// </summary>
    public const string DefaultValue = "default";

    /// <summary>
    /// The default profile, used whenever the request carries no OAuth2 identity.
    /// </summary>
    public static ProfileId Default { get; } = new(DefaultValue);

    /// <summary>
    /// The raw profile id value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Creates a new <see cref="ProfileId"/>. Throws if <paramref name="value"/> is null or whitespace.
    /// </summary>
    public ProfileId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Profile id must not be null or whitespace.", nameof(value));
        }

        Value = value;
    }

    /// <summary>
    /// Returns true when this id refers to the built-in default profile.
    /// </summary>
    public bool IsDefault => string.Equals(Value, DefaultValue, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override string ToString() => Value;
}
