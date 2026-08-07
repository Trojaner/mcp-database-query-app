using System.Text.Json;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Core.Tasks;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace McpDatabaseQueryApp.Server.Tasks;

/// <summary>
/// Durable <see cref="IMcpTaskStore"/> backed by the existing SQLite metadata store.
/// </summary>
/// <remarks>
/// <para>
/// Replaces the SDK's <see cref="InMemoryMcpTaskStore"/> so task state survives a process
/// restart: a client that holds a task handle and polls <c>tasks/get</c> gets a truthful
/// answer instead of "unknown task" after a redeploy.
/// </para>
/// <para>
/// <b>Execution is not resumed across a restart</b> — only the record is durable. Rows left
/// mid-flight by a crash are reconciled to <see cref="McpTaskState.Failed"/> at startup by
/// <see cref="McpTaskReconciler"/>, so no client can poll a task that nothing is working on.
/// </para>
/// <para>
/// <b>Profile scoping:</b> reads made inside a request scope are filtered to the caller's
/// profile, so a handle minted for one profile cannot be resolved by another. Writes and
/// reads made from the background execution continuation — which has no ambient profile —
/// address the row by its opaque server-minted id.
/// </para>
/// </remarks>
public sealed class SqliteMcpTaskStore : IMcpTaskStore
{
    private readonly IMetadataStore _store;
    private readonly IProfileContextAccessor _profile;
    private readonly McpTaskOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SqliteMcpTaskStore> _logger;

    public SqliteMcpTaskStore(
        IMetadataStore store,
        IProfileContextAccessor profile,
        McpTaskOptions options,
        TimeProvider timeProvider,
        ILogger<SqliteMcpTaskStore> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _profile = profile;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>
    /// Raised when a client supplies an answer via <c>tasks/update</c>, so the suspended tool
    /// body can resume. Subscribed by the SDK.
    /// </summary>
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    /// <inheritdoc/>
    public async Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();

        // The handle is the only thing the client holds and it is the sole authorisation to
        // read the task, so it must be unguessable rather than sequential.
        var taskId = Guid.NewGuid().ToString("N");

        var record = new McpTaskRecord(
            taskId,
            _profile.CurrentIdOrDefault.Value,
            McpTaskState.Working,
            StatusMessage: null,
            CreatedAt: now,
            LastUpdatedAt: now,
            ExpiresAt: now + _options.TimeToLive,
            PollIntervalMs: (long)_options.PollInterval.TotalMilliseconds,
            ResultJson: null,
            ErrorJson: null,
            InputRequestsJson: null);

        await _store.InsertTaskAsync(record, cancellationToken).ConfigureAwait(false);
        _logger.LogDebug("Created task {TaskId}", taskId);
        return ToInfo(record, now);
    }

    /// <inheritdoc/>
    public async Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        var record = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();

        // Treat an elapsed TTL as gone even if the janitor has not swept yet, so behaviour
        // does not depend on sweep timing.
        return record.ExpiresAt <= now ? null : ToInfo(record, now);
    }

    /// <inheritdoc/>
    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken) =>
        TransitionAsync(
            taskId,
            record => record with
            {
                State = McpTaskState.Completed,
                ResultJson = result.GetRawText(),
                InputRequestsJson = null,
            },
            cancellationToken);

    /// <inheritdoc/>
    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken) =>
        TransitionAsync(
            taskId,
            record => record with
            {
                State = McpTaskState.Failed,
                ErrorJson = error.GetRawText(),
                InputRequestsJson = null,
            },
            cancellationToken);

    /// <inheritdoc/>
    public async Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken)
    {
        var record = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return false;
        }

        // Terminal states are final — cancelling a finished task must not rewrite its outcome.
        if (record.State is McpTaskState.Completed or McpTaskState.Failed or McpTaskState.Cancelled)
        {
            return false;
        }

        var now = _timeProvider.GetUtcNow();
        return await _store.UpdateTaskAsync(
            record with
            {
                State = McpTaskState.Cancelled,
                LastUpdatedAt = now,
                InputRequestsJson = null,
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputRequests);
        var json = JsonSerializer.Serialize(inputRequests, McpTasksJsonContext.Default.IDictionaryStringInputRequest);

        return TransitionAsync(
            taskId,
            record => record with
            {
                State = McpTaskState.InputRequired,
                InputRequestsJson = json,
            },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputResponses);

        await TransitionAsync(
            taskId,
            record => record with
            {
                State = McpTaskState.Working,
                InputRequestsJson = null,
            },
            cancellationToken).ConfigureAwait(false);

        // Wake the suspended tool body. Raised after the state is persisted so a resumed
        // execution can never observe the task still sitting in InputRequired.
        var handler = InputResponseReceived;
        if (handler is null)
        {
            return;
        }


        foreach (var (requestId, response) in inputResponses)
        {
            handler(new InputResponseReceivedEventArgs
            {
                TaskId = taskId,
                RequestId = requestId,
                Response = response,
            });
        }
    }

    private async Task<McpTaskRecord?> LoadAsync(string taskId, CancellationToken cancellationToken)
    {
        // A null ambient profile means there is no request scope — the background execution
        // continuation completing its own task. Client-facing calls always run inside a scope
        // and are therefore filtered.
        var profileId = _profile.Current?.Id.Value;
        return await _store.GetTaskAsync(taskId, profileId, cancellationToken).ConfigureAwait(false);
    }

    private async Task TransitionAsync(
        string taskId,
        Func<McpTaskRecord, McpTaskRecord> transition,
        CancellationToken cancellationToken)
    {
        var record = await LoadAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            _logger.LogWarning("Task {TaskId} no longer exists; state transition dropped", taskId);
            return;
        }

        var updated = transition(record) with { LastUpdatedAt = _timeProvider.GetUtcNow() };
        if (!await _store.UpdateTaskAsync(updated, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("Task {TaskId} disappeared during a state transition", taskId);
        }
    }

    private static McpTaskInfo ToInfo(McpTaskRecord record, DateTimeOffset now)
    {
        var remaining = record.ExpiresAt - now;
        return new McpTaskInfo(
            record.TaskId,
            ToStatus(record.State),
            record.CreatedAt,
            record.LastUpdatedAt,
            remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero,
            record.PollIntervalMs,
            record.StatusMessage,
            Parse(record.ResultJson),
            Parse(record.ErrorJson),
            ParseInputRequests(record.InputRequestsJson));
    }

    private static McpTaskStatus ToStatus(McpTaskState state) => state switch
    {
        McpTaskState.Working => McpTaskStatus.Working,
        McpTaskState.InputRequired => McpTaskStatus.InputRequired,
        McpTaskState.Completed => McpTaskStatus.Completed,
        McpTaskState.Cancelled => McpTaskStatus.Cancelled,
        _ => McpTaskStatus.Failed,
    };

    private static JsonElement? Parse(string? json) =>
        json is null ? null : JsonDocument.Parse(json).RootElement.Clone();

    private static IReadOnlyDictionary<string, InputRequest>? ParseInputRequests(string? json) =>
        json is null
            ? null
            : JsonSerializer.Deserialize(json, McpTasksJsonContext.Default.IDictionaryStringInputRequest)
                is { } parsed
                ? new Dictionary<string, InputRequest>(parsed, StringComparer.Ordinal)
                : null;
}
