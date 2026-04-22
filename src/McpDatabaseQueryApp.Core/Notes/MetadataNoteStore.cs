using McpDatabaseQueryApp.Core.Storage;

namespace McpDatabaseQueryApp.Core.Notes;

public sealed class MetadataNoteStore : INoteStore
{
    private readonly IMetadataStore _store;

    public MetadataNoteStore(IMetadataStore store)
    {
        _store = store;
    }

    public Task<NoteRecord?> GetAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken) =>
        _store.GetNoteAsync(targetType, targetPath, cancellationToken);

    public Task<(IReadOnlyList<NoteRecord> Items, long Total)> ListAsync(
        NoteTargetType? targetType,
        string? pathPrefix,
        int offset,
        int limit,
        CancellationToken cancellationToken) =>
        _store.ListNotesAsync(targetType, pathPrefix, offset, limit, cancellationToken);

    public Task<NoteRecord> UpsertAsync(NoteRecord note, CancellationToken cancellationToken) =>
        _store.UpsertNoteAsync(note, cancellationToken);

    public Task<bool> DeleteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken) =>
        _store.DeleteNoteAsync(targetType, targetPath, cancellationToken);

    public Task<IReadOnlyDictionary<string, NoteRecord>> GetBulkAsync(
        NoteTargetType targetType,
        IReadOnlyList<string> targetPaths,
        CancellationToken cancellationToken) =>
        _store.GetNotesBulkAsync(targetType, targetPaths, cancellationToken);
}
