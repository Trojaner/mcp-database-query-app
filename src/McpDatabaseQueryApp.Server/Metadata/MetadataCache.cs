using System.Collections.Concurrent;
using McpDatabaseQueryApp.Core.Providers;

namespace McpDatabaseQueryApp.Server.Metadata;

public sealed class MetadataCache
{
    private readonly ConcurrentDictionary<string, CachedConnectionMetadata> _byConnection = new(StringComparer.Ordinal);

    public CachedConnectionMetadata GetOrCreate(string connectionId) =>
        _byConnection.GetOrAdd(connectionId, _ => new CachedConnectionMetadata());

    public void Forget(string connectionId) => _byConnection.TryRemove(connectionId, out _);

    public IReadOnlyCollection<string> Connections => [.. _byConnection.Keys];
}

public sealed class CachedConnectionMetadata
{
    public IReadOnlyList<SchemaInfo> Schemas { get; set; } = [];

    public IReadOnlyDictionary<string, IReadOnlyList<TableInfo>> Tables { get; set; } = new Dictionary<string, IReadOnlyList<TableInfo>>(StringComparer.OrdinalIgnoreCase);

    public DateTimeOffset LastRefreshed { get; set; }
}
