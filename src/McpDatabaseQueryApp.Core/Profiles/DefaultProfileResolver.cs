namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Always returns the built-in default profile. Used as the always-succeeding
/// fallback at the end of the resolver chain, and as the sole resolver for
/// transports that have no concept of client identity (e.g. stdio).
/// </summary>
public sealed class DefaultProfileResolver : IProfileResolver
{
    private readonly IProfileStore _profiles;

    /// <summary>
    /// Creates a new <see cref="DefaultProfileResolver"/>.
    /// </summary>
    public DefaultProfileResolver(IProfileStore profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        _profiles = profiles;
    }

    /// <inheritdoc/>
    public async Task<Profile?> ResolveAsync(IProfileAuthContext context, CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(ProfileId.Default, cancellationToken).ConfigureAwait(false);
        if (profile is not null)
        {
            return profile;
        }

        // Defensive: the schema migration seeds this row, but if a deployment
        // pruned it we re-create on demand to keep the server reachable.
        var seeded = new Profile(
            ProfileId.Default,
            "default",
            ProfileId.DefaultValue,
            Issuer: null,
            DateTimeOffset.UtcNow,
            ProfileStatus.Active,
            new Dictionary<string, string>(StringComparer.Ordinal));
        return await _profiles.UpsertAsync(seeded, cancellationToken).ConfigureAwait(false);
    }
}
