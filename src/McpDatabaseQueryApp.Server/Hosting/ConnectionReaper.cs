using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Server.Metadata;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.Hosting;

public sealed class ConnectionReaper : BackgroundService
{
    private readonly IConnectionRegistry _registry;
    private readonly ConnectionActivityTracker _tracker;
    private readonly MetadataCache _metadataCache;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly ILogger<ConnectionReaper> _logger;
    private readonly TimeSpan _period = TimeSpan.FromMinutes(5);

    public ConnectionReaper(
        IConnectionRegistry registry,
        ConnectionActivityTracker tracker,
        MetadataCache metadataCache,
        McpDatabaseQueryAppOptions options,
        ILogger<ConnectionReaper> logger)
    {
        _registry = registry;
        _tracker = tracker;
        _metadataCache = metadataCache;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var idle = _tracker.GetIdleConnections(_options.ConnectionIdleTimeout);
                foreach (var connectionId in idle)
                {
                    var removed = await _registry.ReapAsync(connectionId).ConfigureAwait(false);
                    if (removed)
                    {
                        _tracker.Remove(connectionId);
                        _metadataCache.Forget(connectionId);
                        _logger.LogInformation("Reaped idle connection {ConnectionId}", connectionId);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Failed to reap idle connections");
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
