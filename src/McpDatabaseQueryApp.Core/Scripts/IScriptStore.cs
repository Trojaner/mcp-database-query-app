using McpDatabaseQueryApp.Core.Storage;

namespace McpDatabaseQueryApp.Core.Scripts;

public interface IScriptStore
{
    Task<ScriptRecord?> GetAsync(string nameOrId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ScriptRecord> Items, long Total)> ListAsync(int offset, int limit, string? filter, CancellationToken cancellationToken);

    Task<ScriptRecord> UpsertAsync(ScriptRecord script, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(string nameOrId, CancellationToken cancellationToken);
}

public sealed class MetadataScriptStore : IScriptStore
{
    private readonly IMetadataStore _metadata;

    public MetadataScriptStore(IMetadataStore metadata)
    {
        _metadata = metadata;
    }

    public Task<ScriptRecord?> GetAsync(string nameOrId, CancellationToken cancellationToken)
        => _metadata.GetScriptAsync(nameOrId, cancellationToken);

    public Task<(IReadOnlyList<ScriptRecord> Items, long Total)> ListAsync(int offset, int limit, string? filter, CancellationToken cancellationToken)
        => _metadata.ListScriptsAsync(offset, limit, filter, cancellationToken);

    public Task<ScriptRecord> UpsertAsync(ScriptRecord script, CancellationToken cancellationToken)
        => _metadata.UpsertScriptAsync(script, cancellationToken);

    public Task<bool> DeleteAsync(string nameOrId, CancellationToken cancellationToken)
        => _metadata.DeleteScriptAsync(nameOrId, cancellationToken);
}
