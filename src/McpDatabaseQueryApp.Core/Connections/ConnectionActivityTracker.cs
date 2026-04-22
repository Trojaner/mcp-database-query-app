using System.Collections.Concurrent;

namespace McpDatabaseQueryApp.Core.Connections;

public sealed class ConnectionActivityTracker
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsed = new(StringComparer.Ordinal);

    public void Touch(string connectionId)
    {
        _lastUsed[connectionId] = DateTimeOffset.UtcNow;
    }

    public void Remove(string connectionId)
    {
        _lastUsed.TryRemove(connectionId, out _);
    }

    public IReadOnlyList<string> GetIdleConnections(TimeSpan threshold)
    {
        var cutoff = DateTimeOffset.UtcNow - threshold;
        return _lastUsed
            .Where(kvp => kvp.Value < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();
    }
}
