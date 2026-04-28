using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Server.IntegrationTests.Profiles;

/// <summary>
/// Test-only <see cref="IProfileResolver"/> that hands out a configurable
/// <see cref="Profile"/>. The harness swaps this in for the production
/// composite resolver so tests can drive deterministic profile switches via
/// <see cref="InProcessServerHarness.UseProfile"/> without spinning up an
/// OIDC issuer.
/// </summary>
public sealed class TestProfileResolver : IProfileResolver
{
    private Profile? _current;

    /// <summary>
    /// Creates a resolver pre-bound to <paramref name="initial"/>.
    /// </summary>
    public TestProfileResolver(Profile initial)
    {
        _current = initial ?? throw new ArgumentNullException(nameof(initial));
    }

    /// <summary>
    /// Replaces the profile that subsequent <see cref="ResolveAsync"/> calls
    /// will return. Volatile write so worker threads see the new value
    /// without locking.
    /// </summary>
    public void Set(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Volatile.Write(ref _current, profile);
    }

    /// <inheritdoc/>
    public Task<Profile?> ResolveAsync(IProfileAuthContext context, CancellationToken cancellationToken)
    {
        return Task.FromResult<Profile?>(Volatile.Read(ref _current));
    }
}
