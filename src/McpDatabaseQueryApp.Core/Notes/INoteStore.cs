namespace McpDatabaseQueryApp.Core.Notes;

public interface INoteStore
{
    Task<NoteRecord?> GetAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken);

    Task<(IReadOnlyList<NoteRecord> Items, long Total)> ListAsync(
        NoteTargetType? targetType,
        string? pathPrefix,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<NoteRecord> UpsertAsync(NoteRecord note, CancellationToken cancellationToken);

    Task<bool> DeleteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, NoteRecord>> GetBulkAsync(
        NoteTargetType targetType,
        IReadOnlyList<string> targetPaths,
        CancellationToken cancellationToken);
}
