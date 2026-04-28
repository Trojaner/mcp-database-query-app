namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Resolves an inbound request to the <see cref="Profile"/> whose data and
/// keys the request is allowed to address. Implementations form a chain
/// (strategy pattern): typically an OAuth2-aware resolver runs first, with
/// a default-profile resolver as the always-succeeding fallback.
/// </summary>
public interface IProfileResolver
{
    /// <summary>
    /// Returns the profile to use for this request, or <c>null</c> if this
    /// resolver does not apply (so a downstream resolver in the chain can
    /// take over).
    /// </summary>
    Task<Profile?> ResolveAsync(IProfileAuthContext context, CancellationToken cancellationToken);
}
