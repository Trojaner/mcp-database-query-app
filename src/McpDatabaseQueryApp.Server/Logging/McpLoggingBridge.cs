using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Logging;

public sealed class McpLoggingBridge
{
    public LoggingLevel CurrentLevel { get; private set; } = LoggingLevel.Info;

    public ValueTask<EmptyResult> HandleSetLevelAsync(
        RequestContext<SetLevelRequestParams> context,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        if (context.Params is { Level: var level })
        {
            CurrentLevel = level;
        }

        return ValueTask.FromResult(new EmptyResult());
    }
}
