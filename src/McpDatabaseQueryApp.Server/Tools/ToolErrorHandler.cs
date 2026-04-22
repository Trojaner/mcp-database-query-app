using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.Tools;

public static class ToolErrorHandler
{
    public static async Task<T> WrapAsync<T>(Func<Task<T>> action, ILogger logger)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tool execution failed");
            throw new InvalidOperationException(Sanitize(ex));
        }
    }

    public static T Wrap<T>(Func<T> action, ILogger logger)
    {
        try
        {
            return action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Tool execution failed");
            throw new InvalidOperationException(Sanitize(ex));
        }
    }

    private static string Sanitize(Exception ex)
    {
        return ex switch
        {
            KeyNotFoundException e => $"Not found: {e.Message}",
            ArgumentException e => $"Validation error: {e.Message}",
            InvalidOperationException e => e.Message,
            TimeoutException => "Query timed out.",
            _ => ContainsSensitiveInfo(ex.Message)
                ? "A database error occurred. Check server logs for details."
                : ex.Message,
        };
    }

    private static bool ContainsSensitiveInfo(string message) =>
        message.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Server=", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("User ID=", StringComparison.OrdinalIgnoreCase) ||
        message.Contains("Data Source=", StringComparison.OrdinalIgnoreCase);
}
