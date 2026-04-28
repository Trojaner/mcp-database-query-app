namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Persistence surface for profiles. Implementations are expected to share the
/// same SQLite metadata database that backs <see cref="Storage.IMetadataStore"/>
/// so that profile rows live in the same transactional store as the data they
/// scope.
/// </summary>
public interface IProfileStore
{
    /// <summary>
    /// Ensures the built-in default profile exists. Safe to call on every
    /// startup; idempotent.
    /// </summary>
    Task EnsureDefaultAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new profile or updates the existing record (matched by <see cref="Profile.Id"/>).
    /// </summary>
    Task<Profile> UpsertAsync(Profile profile, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the profile with the given id, or <c>null</c> if none exists.
    /// </summary>
    Task<Profile?> GetAsync(ProfileId id, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the profile that matches the provided OAuth2 <c>(issuer, subject)</c>
    /// pair, or <c>null</c> if no profile has been provisioned for that identity.
    /// </summary>
    Task<Profile?> FindByIdentityAsync(string? issuer, string subject, CancellationToken cancellationToken);

    /// <summary>
    /// Lists all profiles, ordered by created-at ascending.
    /// </summary>
    Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a profile and (transitively) every row scoped to it.
    /// Refuses to delete the built-in default profile.
    /// </summary>
    /// <returns>True if a profile was deleted; false otherwise.</returns>
    Task<bool> DeleteAsync(ProfileId id, CancellationToken cancellationToken);
}
