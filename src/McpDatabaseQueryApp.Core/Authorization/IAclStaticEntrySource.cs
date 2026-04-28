using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Supplies static, config-driven ACL entries to the evaluator. Static
/// entries are NOT persisted to SQLite and the REST API cannot mutate them;
/// they are loaded from <c>appsettings.json</c> at startup and live for the
/// lifetime of the process. The evaluator merges them on top of stored
/// entries using a +1000 priority offset so they always outrank operator-
/// managed rules.
/// </summary>
/// <remarks>
/// The Server-side <c>AclBootstrapHostedService</c> registers an
/// implementation that reads <see cref="AuthorizationOptions.StaticEntries"/>.
/// Tests may register a stub. When no implementation is registered the
/// evaluator silently treats the static set as empty.
/// </remarks>
public interface IAclStaticEntrySource
{
    /// <summary>
    /// Returns the static entries that apply to <paramref name="profile"/>.
    /// Implementations must be safe to call from concurrent request threads
    /// and should be cheap (typically a dictionary lookup); they are invoked
    /// on every cache miss in the evaluator.
    /// </summary>
    IReadOnlyList<AclEntry> GetEntriesFor(ProfileId profile);
}
