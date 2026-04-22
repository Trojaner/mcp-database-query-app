using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Scripts;

namespace McpDatabaseQueryApp.Core.Storage;

public sealed record DatabaseRecord(
    ConnectionDescriptor Descriptor,
    byte[] PasswordCipher,
    byte[] PasswordNonce);

public interface IMetadataStore
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<DatabaseRecord?> GetDatabaseAsync(string nameOrId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ConnectionDescriptor> Items, long Total)> ListDatabasesAsync(int offset, int limit, string? filter, CancellationToken cancellationToken);

    Task<ConnectionDescriptor> UpsertDatabaseAsync(ConnectionDescriptor descriptor, byte[] passwordCipher, byte[] passwordNonce, CancellationToken cancellationToken);

    Task<ConnectionDescriptor> UpdateDatabaseMetadataAsync(ConnectionDescriptor descriptor, CancellationToken cancellationToken);

    Task<bool> DeleteDatabaseAsync(string nameOrId, CancellationToken cancellationToken);

    Task<ScriptRecord?> GetScriptAsync(string nameOrId, CancellationToken cancellationToken);

    Task<(IReadOnlyList<ScriptRecord> Items, long Total)> ListScriptsAsync(int offset, int limit, string? filter, CancellationToken cancellationToken);

    Task<ScriptRecord> UpsertScriptAsync(ScriptRecord script, CancellationToken cancellationToken);

    Task<bool> DeleteScriptAsync(string nameOrId, CancellationToken cancellationToken);

    Task<Results.ResultSetRecord?> GetResultSetAsync(string id, CancellationToken cancellationToken);

    Task InsertResultSetAsync(Results.ResultSetRecord record, CancellationToken cancellationToken);

    Task PurgeExpiredResultSetsAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<NoteRecord?> GetNoteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken);

    Task<(IReadOnlyList<NoteRecord> Items, long Total)> ListNotesAsync(
        NoteTargetType? targetType,
        string? pathPrefix,
        int offset,
        int limit,
        CancellationToken cancellationToken);

    Task<NoteRecord> UpsertNoteAsync(NoteRecord note, CancellationToken cancellationToken);

    Task<bool> DeleteNoteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, NoteRecord>> GetNotesBulkAsync(
        NoteTargetType targetType,
        IReadOnlyList<string> targetPaths,
        CancellationToken cancellationToken);
}
