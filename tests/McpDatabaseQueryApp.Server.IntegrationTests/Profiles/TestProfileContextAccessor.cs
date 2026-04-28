using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Server.IntegrationTests.Profiles;

/// <summary>
/// Test-only <see cref="IProfileContextAccessor"/> whose ambient profile is a
/// process-wide volatile field rather than an <see cref="AsyncLocal{T}"/>
/// stack. The in-process harness drives the MCP server on a background task
/// whose <c>ExecutionContext</c> is captured once at <c>RunAsync</c> time;
/// switching the profile from the test thread (via
/// <see cref="InProcessServerHarness.UseProfile"/>) must be visible to those
/// already-running continuations, which a normal AsyncLocal cannot guarantee.
/// </summary>
/// <remarks>
/// This accessor is only registered when the harness is started with
/// <see cref="InProcessServerHarness.StartAsync"/>'s test-resolver branch.
/// Production code uses <see cref="ProfileContextAccessor"/> unchanged.
/// </remarks>
public sealed class TestProfileContextAccessor : IProfileContextAccessor
{
    private Profile? _current;

    /// <summary>
    /// Sets the ambient profile that all subsequent <see cref="Current"/>
    /// reads (on any thread) will see.
    /// </summary>
    public void Set(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Volatile.Write(ref _current, profile);
    }

    /// <inheritdoc/>
    public Profile? Current => Volatile.Read(ref _current);

    /// <inheritdoc/>
    public IProfileScope Begin(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var prior = Volatile.Read(ref _current);
        Volatile.Write(ref _current, profile);
        return new Scope(this, prior, profile);
    }

    private sealed class Scope : IProfileScope
    {
        private readonly TestProfileContextAccessor _owner;
        private readonly Profile? _prior;
        private bool _disposed;

        public Scope(TestProfileContextAccessor owner, Profile? prior, Profile profile)
        {
            _owner = owner;
            _prior = prior;
            Profile = profile;
        }

        public Profile Profile { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_prior is null)
            {
                _owner._current = null;
            }
            else
            {
                Volatile.Write(ref _owner._current, _prior);
            }
        }
    }
}
