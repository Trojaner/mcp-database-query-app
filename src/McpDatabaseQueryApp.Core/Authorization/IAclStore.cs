using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Persistence-side contract for ACL entries. Mutating methods are reserved
/// for the operator-facing REST API (Task 6); MCP tooling consumes
/// <see cref="ListAsync"/> only via the in-memory cache layered on top by
/// <see cref="IAclEvaluator"/>.
/// </summary>
public interface IAclStore
{
    /// <summary>
    /// Returns every entry for <paramref name="profile"/>, ordered for
    /// deterministic display. Callers must not assume any priority ordering;
    /// the evaluator re-sorts before matching.
    /// </summary>
    Task<IReadOnlyList<AclEntry>> ListAsync(ProfileId profile, CancellationToken cancellationToken);

    /// <summary>
    /// Returns a single entry by id, or <c>null</c> if no row exists. The
    /// row's profile is intentionally not filtered here — the REST API uses
    /// this to look up an entry before authorizing the caller.
    /// </summary>
    Task<AclEntry?> GetAsync(AclEntryId id, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts a new entry or updates the existing row with the same id. The
    /// profile id on <paramref name="entry"/> is authoritative; the row is
    /// always persisted under that profile regardless of the ambient one.
    /// </summary>
    Task<AclEntry> UpsertAsync(AclEntry entry, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the row with id <paramref name="id"/> within
    /// <paramref name="profile"/>. Returns <c>true</c> when a row was
    /// removed.
    /// </summary>
    Task<bool> DeleteAsync(ProfileId profile, AclEntryId id, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces every entry for <paramref name="profile"/> with the supplied
    /// set in a single transaction. Useful for the REST API's bulk-set
    /// endpoint.
    /// </summary>
    Task ReplaceAllAsync(ProfileId profile, IReadOnlyList<AclEntry> entries, CancellationToken cancellationToken);
}
