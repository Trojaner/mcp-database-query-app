namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Transport-neutral abstraction over the inbound request's authentication
/// state. Implementations live in the transport layer (the HTTP one wraps
/// <c>HttpContext.User</c>; the stdio one is always anonymous).
/// </summary>
/// <remarks>
/// <para>
/// This abstraction lets the <see cref="IProfileResolver"/> chain run without
/// taking a dependency on ASP.NET, keeping <c>Core</c> framework-free per the
/// layering rules.
/// </para>
/// <para>
/// Implementations must NOT expose the bearer token itself or any PII-bearing
/// claim values. Only the OAuth2 identifier pair <c>(issuer, subject)</c>
/// plus a small whitelist of profile-naming claims is surfaced.
/// </para>
/// </remarks>
public interface IProfileAuthContext
{
    /// <summary>
    /// True when the inbound request carries a validated bearer token.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>
    /// The OAuth2 <c>iss</c> claim, or <c>null</c> if no token was presented
    /// or the issuer claim is absent.
    /// </summary>
    string? Issuer { get; }

    /// <summary>
    /// The OAuth2 <c>sub</c> claim, or <c>null</c> if no token was presented.
    /// Populated only when <see cref="IsAuthenticated"/> is true.
    /// </summary>
    string? Subject { get; }

    /// <summary>
    /// A friendly display name to use when auto-provisioning a profile.
    /// Typically resolves to the <c>preferred_username</c> claim, falling
    /// back to <see cref="Subject"/>.
    /// </summary>
    string? PreferredName { get; }
}

/// <summary>
/// Anonymous auth context that always reports unauthenticated. Used by the
/// stdio transport (which has no client identity) and as a sentinel inside
/// tests.
/// </summary>
public sealed class AnonymousProfileAuthContext : IProfileAuthContext
{
    /// <summary>Singleton instance.</summary>
    public static AnonymousProfileAuthContext Instance { get; } = new();

    private AnonymousProfileAuthContext()
    {
    }

    /// <inheritdoc/>
    public bool IsAuthenticated => false;

    /// <inheritdoc/>
    public string? Issuer => null;

    /// <inheritdoc/>
    public string? Subject => null;

    /// <inheritdoc/>
    public string? PreferredName => null;
}
