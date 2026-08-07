using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpDatabaseQueryApp.Server.Tools;

public static class ToolErrorHandler
{
    public static async Task<T> WrapAsync<T>(Func<Task<T>> action, ILogger logger)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsControlFlow(ex))
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
        catch (Exception ex) when (!IsControlFlow(ex))
        {
            logger.LogError(ex, "Tool execution failed");
            throw new InvalidOperationException(Sanitize(ex));
        }
    }

    /// <summary>
    /// Exceptions that carry protocol meaning rather than a failure, and so must
    /// reach the SDK unmodified.
    /// </summary>
    /// <remarks>
    /// <see cref="InputRequiredException"/> is how a tool asks the client for more
    /// input under the multi round-trip request pattern (MCP 2026-07-28, SEP-2322).
    /// The SDK turns it into an <c>input_required</c> result; converting it to an
    /// <see cref="InvalidOperationException"/> here would surface every confirmation
    /// prompt as a tool error instead.
    /// </remarks>
    private static bool IsControlFlow(Exception ex) =>
        ex is OperationCanceledException or InputRequiredException or UrlElicitationRequiredException;

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
