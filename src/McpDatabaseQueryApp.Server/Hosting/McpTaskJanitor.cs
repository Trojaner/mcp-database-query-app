using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Server.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.Hosting;

/// <summary>
/// Periodically deletes task records whose TTL has elapsed, so the tasks table cannot grow
/// without bound on a long-lived server. Mirrors <see cref="ResultSetJanitor"/>.
/// </summary>
public sealed class McpTaskJanitor : BackgroundService
{
    private readonly IMetadataStore _store;
    private readonly McpTaskOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpTaskJanitor> _logger;

    public McpTaskJanitor(
        IMetadataStore store,
        McpTaskOptions options,
        TimeProvider timeProvider,
        ILogger<McpTaskJanitor> logger)
    {
        _store = store;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var purged = await _store
                    .PurgeExpiredTasksAsync(_timeProvider.GetUtcNow(), stoppingToken)
                    .ConfigureAwait(false);

                if (purged > 0)
                {
                    _logger.LogDebug("Purged {Count} expired task record(s)", purged);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to purge expired tasks");
            }

            try
            {
                await Task.Delay(_options.JanitorPeriod, _timeProvider, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }
}
