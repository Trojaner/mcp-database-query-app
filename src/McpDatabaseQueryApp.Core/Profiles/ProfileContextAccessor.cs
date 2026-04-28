namespace McpDatabaseQueryApp.Core.Profiles;

/// <summary>
/// Default <see cref="IProfileContextAccessor"/> backed by a per-execution-flow
/// <see cref="System.Threading.AsyncLocal{T}"/> stack so that nested
/// <see cref="Begin"/> calls compose cleanly and cross task boundaries.
/// </summary>
public sealed class ProfileContextAccessor : IProfileContextAccessor
{
    private static readonly AsyncLocal<ScopeFrame?> Holder = new();

    /// <inheritdoc/>
    public Profile? Current => Holder.Value?.Profile;

    /// <inheritdoc/>
    public IProfileScope Begin(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var prior = Holder.Value;
        var frame = new ScopeFrame(profile, prior, this);
        Holder.Value = frame;
        return frame;
    }

    private sealed class ScopeFrame : IProfileScope
    {
        private readonly ScopeFrame? _prior;
        private readonly ProfileContextAccessor _accessor;
        private bool _disposed;

        public ScopeFrame(Profile profile, ScopeFrame? prior, ProfileContextAccessor accessor)
        {
            Profile = profile;
            _prior = prior;
            _accessor = accessor;
        }

        public Profile Profile { get; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Only pop if we're still the top of stack on this flow. If a caller
            // forgot to dispose in nested order we leave the AsyncLocal alone
            // rather than clobbering an unrelated frame.
            if (ReferenceEquals(Holder.Value, this))
            {
                Holder.Value = _prior;
            }

            _ = _accessor; // suppress unused-field warning; kept for diagnostics.
        }
    }
}
