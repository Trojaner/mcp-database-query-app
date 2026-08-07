using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Core.Tasks;

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

    // --- MCP tasks extension (io.modelcontextprotocol/tasks) ---

    /// <summary>Persists a newly minted task in the <see cref="McpTaskState.Working"/> state.</summary>
    Task InsertTaskAsync(McpTaskRecord record, CancellationToken cancellationToken);

    /// <summary>
    /// Loads a task by its opaque handle. When <paramref name="profileId"/> is non-null the
    /// row is only returned if it belongs to that profile, so one caller cannot read another
    /// profile's task by guessing a handle.
    /// </summary>
    Task<McpTaskRecord?> GetTaskAsync(string taskId, string? profileId, CancellationToken cancellationToken);

    /// <summary>Overwrites the mutable state of an existing task. Returns false if it no longer exists.</summary>
    Task<bool> UpdateTaskAsync(McpTaskRecord record, CancellationToken cancellationToken);

    /// <summary>Deletes tasks whose TTL has elapsed. Runs across all profiles.</summary>
    Task<int> PurgeExpiredTasksAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    /// Marks every task still recorded as running as failed. Task execution cannot survive a
    /// process restart, so any such row is an orphan from a crash or shutdown; leaving it
    /// <see cref="McpTaskState.Working"/> would make clients poll forever.
    /// </summary>
    Task<int> FailInterruptedTasksAsync(string reason, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, NoteRecord>> GetNotesBulkAsync(
        NoteTargetType targetType,
        IReadOnlyList<string> targetPaths,
        CancellationToken cancellationToken);
}
