namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Lifecycle status of a <see cref="Profile"/>.
/// </summary>
public enum ProfileStatus
{
    /// <summary>The profile is active and can be used to authorize requests.</summary>
    Active = 0,

    /// <summary>The profile is disabled. Lookups still resolve, but tools must refuse to operate.</summary>
    Disabled = 1,
}

/// <summary>
/// A profile is the OAuth2-identity-scoped sandbox that owns its own databases,
/// connections, notes, scripts and cached result sets. Profile resolution happens
/// per inbound request: an authenticated bearer token maps to a profile via the
/// <c>(issuer, subject)</c> claim pair; absence of an auth context resolves to
/// the built-in default profile.
/// </summary>
/// <param name="Id">Strongly-typed profile identifier.</param>
/// <param name="Name">Human readable name shown to operators (defaults to <c>preferred_username</c> or the <c>sub</c> claim).</param>
/// <param name="Subject">The OAuth2 <c>sub</c> claim, or <c>"default"</c> for the built-in profile.</param>
/// <param name="Issuer">The OAuth2 <c>iss</c> claim, or <c>null</c> for the built-in profile.</param>
/// <param name="CreatedAt">Provisioning timestamp.</param>
/// <param name="Status">Lifecycle status (see <see cref="ProfileStatus"/>).</param>
/// <param name="Metadata">Free-form extension fields, persisted as JSON.</param>
public sealed record Profile(
    ProfileId Id,
    string Name,
    string Subject,
    string? Issuer,
    DateTimeOffset CreatedAt,
    ProfileStatus Status,
    IReadOnlyDictionary<string, string> Metadata);
