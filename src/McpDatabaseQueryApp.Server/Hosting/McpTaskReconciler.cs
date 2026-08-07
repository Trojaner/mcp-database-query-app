using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Server.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.Hosting;

/// <summary>
/// At startup, fails every task still recorded as running or awaiting input.
/// </summary>
/// <remarks>
/// Task records are durable but task <i>execution</i> is not: nothing resumes a half-finished
/// query after the process dies. Any row still in a non-terminal state at startup is therefore
/// an orphan of a crash or an ungraceful shutdown. Without this, a client holding its handle
/// would poll <c>tasks/get</c> forever against work that no longer exists. Failing them makes
/// the outcome explicit and lets the client retry.
/// </remarks>
public sealed class McpTaskReconciler : IHostedService
{
    private const string Reason = "Server restarted while this task was still running; it did not complete.";

    private readonly IMetadataStore _store;
    private readonly McpTaskOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpTaskReconciler> _logger;

    public McpTaskReconciler(
        IMetadataStore store,
        McpTaskOptions options,
        TimeProvider timeProvider,
        ILogger<McpTaskReconciler> logger)
    {
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        try
        {
            var failed = await _store
                .FailInterruptedTasksAsync(Reason, _timeProvider.GetUtcNow(), cancellationToken)
                .ConfigureAwait(false);

            if (failed > 0)
            {
                _logger.LogWarning("Failed {Count} task(s) interrupted by a previous shutdown", failed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Never block startup on reconciliation — a stale task row is far less harmful
            // than a server that will not boot.
            _logger.LogError(ex, "Could not reconcile interrupted tasks");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
