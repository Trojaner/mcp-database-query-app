using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.Hosting;

public sealed class ResultSetJanitor : BackgroundService
{
    private readonly IMetadataStore _store;
    private readonly ILogger<ResultSetJanitor> _logger;
    private readonly TimeSpan _period = TimeSpan.FromMinutes(5);

    public ResultSetJanitor(IMetadataStore store, ILogger<ResultSetJanitor> logger)
    {
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _store.PurgeExpiredResultSetsAsync(DateTimeOffset.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to purge expired result sets");
            }

            try
            {
                await Task.Delay(_period, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }
    }
}
