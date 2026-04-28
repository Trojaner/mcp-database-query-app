using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Persists <see cref="IsolationRule"/>s. Implementations merge static
/// (in-memory, from config) and dynamic (SQLite) rules behind one surface so
/// the engine sees a single sorted view.
/// </summary>
/// <remarks>
/// Static rules are immutable: <see cref="UpsertAsync"/> and
/// <see cref="DeleteAsync"/> on a static rule throw
/// <see cref="InvalidOperationException"/>.
/// </remarks>
public interface IIsolationRuleStore
{
    /// <summary>
    /// Returns every rule that applies to the supplied profile +
    /// connection, sorted by descending <see cref="IsolationRule.Priority"/>
    /// then by id for stable ordering.
    /// </summary>
    Task<IReadOnlyList<IsolationRule>> ListAsync(
        ProfileId profileId,
        ConnectionDescriptor connection,
        CancellationToken cancellationToken);

    /// <summary>
    /// Looks up a single rule by id. Returns <c>null</c> when the id is
    /// unknown.
    /// </summary>
    Task<IsolationRule?> GetAsync(IsolationRuleId id, CancellationToken cancellationToken);

    /// <summary>
    /// Inserts or updates a dynamic rule. Throws
    /// <see cref="InvalidOperationException"/> when the supplied rule has
    /// <see cref="IsolationRuleSource.Static"/> or when an existing rule
    /// with the same id is static.
    /// </summary>
    Task<IsolationRule> UpsertAsync(IsolationRule rule, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes a dynamic rule. Returns <c>false</c> when the id is unknown.
    /// Throws <see cref="InvalidOperationException"/> when the targeted
    /// rule is static.
    /// </summary>
    Task<bool> DeleteAsync(IsolationRuleId id, CancellationToken cancellationToken);
}
