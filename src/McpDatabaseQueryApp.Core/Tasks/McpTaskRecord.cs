namespace McpDatabaseQueryApp.Core.Tasks;

/// <summary>
/// Lifecycle states of a long-running task.
/// </summary>
/// <remarks>
/// Mirrors the MCP tasks extension status values. Kept as a Core-native enum rather
/// than the SDK's <c>McpTaskStatus</c> so this project stays free of the MCP SDK —
/// the Server layer adapts between the two.
/// </remarks>
public enum McpTaskState
{
    Working,
    InputRequired,
    Completed,
    Cancelled,
    Failed,
}

/// <summary>
/// Persisted state of one long-running task.
/// </summary>
/// <param name="TaskId">Server-minted opaque handle. This is the only thing the client holds.</param>
/// <param name="ProfileId">Owning profile, stamped at creation, used to stop cross-profile reads.</param>
/// <param name="ResultJson">Serialized result once <see cref="McpTaskState.Completed"/>; otherwise null.</param>
/// <param name="ErrorJson">Serialized error once <see cref="McpTaskState.Failed"/>; otherwise null.</param>
/// <param name="InputRequestsJson">
/// Serialized pending input requests while <see cref="McpTaskState.InputRequired"/>. Stored opaquely:
/// Core does not interpret the MCP payload, it only round-trips it.
/// </param>
public sealed record McpTaskRecord(
    string TaskId,
    string ProfileId,
    McpTaskState State,
    string? StatusMessage,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastUpdatedAt,
    DateTimeOffset ExpiresAt,
    long PollIntervalMs,
    string? ResultJson,
    string? ErrorJson,
    string? InputRequestsJson);
