namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Disposable scope returned by <see cref="IProfileContextAccessor.Begin"/>.
/// On dispose, restores the prior ambient profile (or clears it).
/// </summary>
public interface IProfileScope : IDisposable
{
    /// <summary>The profile that is current within this scope.</summary>
    Profile Profile { get; }
}

/// <summary>
/// Ambient access to the profile of the current logical execution context.
/// Backed by <see cref="System.Threading.AsyncLocal{T}"/> so async/await
/// flows through nested calls and tools naturally.
/// </summary>
/// <remarks>
/// <para>
/// The accessor MUST be set before any tool, resource or store operates on
/// per-profile state. The transport layer (HTTP middleware, stdio bootstrap)
/// is responsible for opening a scope at the start of every request.
/// </para>
/// <para>
/// Per-profile stores read the current profile from this accessor instead
/// of accepting it as an explicit argument; this lets existing call sites
/// stay untouched while still being scoped.
/// </para>
/// </remarks>
public interface IProfileContextAccessor
{
    /// <summary>
    /// The current ambient profile, or <c>null</c> if no scope has been
    /// opened on this execution context.
    /// </summary>
    Profile? Current { get; }

    /// <summary>
    /// The current ambient profile id, or <see cref="ProfileId.Default"/>
    /// when no scope is active. Convenience accessor for stores.
    /// </summary>
    ProfileId CurrentIdOrDefault => Current?.Id ?? ProfileId.Default;

    /// <summary>
    /// Opens a scope and sets <paramref name="profile"/> as the current
    /// ambient profile until the returned <see cref="IProfileScope"/> is
    /// disposed.
    /// </summary>
    IProfileScope Begin(Profile profile);
}
