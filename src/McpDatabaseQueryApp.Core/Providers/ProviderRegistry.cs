namespace McpDatabaseQueryApp.Core.Providers;

public interface IProviderRegistry
{
    IDatabaseProvider Get(DatabaseKind kind);

    IReadOnlyList<IDatabaseProvider> All { get; }

    bool TryGet(string providerName, out IDatabaseProvider provider);
}

public sealed class ProviderRegistry : IProviderRegistry
{
    private readonly Dictionary<DatabaseKind, IDatabaseProvider> _byKind;

    public ProviderRegistry(IEnumerable<IDatabaseProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _byKind = providers.ToDictionary(p => p.Kind);
        All = [.. _byKind.Values];
    }

    public IReadOnlyList<IDatabaseProvider> All { get; }

    public IDatabaseProvider Get(DatabaseKind kind)
    {
        if (!_byKind.TryGetValue(kind, out var provider))
        {
            throw new InvalidOperationException($"No provider registered for {kind}.");
        }

        return provider;
    }

    public bool TryGet(string providerName, out IDatabaseProvider provider)
    {
        if (Enum.TryParse<DatabaseKind>(providerName, ignoreCase: true, out var kind)
            && _byKind.TryGetValue(kind, out var match))
        {
            provider = match;
            return true;
        }

        provider = null!;
        return false;
    }
}
