using System.Data.Common;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Server.Elicitation;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace McpDatabaseQueryApp.Server.Tools;

/// <summary>
/// Turns tool failures into <see cref="McpException"/>s so the client actually
/// sees what went wrong.
/// </summary>
/// <remarks>
/// <para>
/// The SDK only forwards an exception's message to the caller when the exception
/// is an <see cref="McpException"/>; every other type collapses to the opaque
/// <c>"An error occurred invoking '&lt;tool&gt;'."</c> placeholder
/// (see <c>McpServerImpl</c>'s call-tool error path). Throwing anything else from
/// a tool therefore hides connection failures, SQL errors, and validation
/// problems from the model, which cannot then self-correct.
/// </para>
/// <para>
/// Because <see cref="McpException.Message"/> crosses the wire, every message is
/// built from a curated description and then run through <see cref="Redact"/> so
/// credentials embedded in driver messages never escape. The original exception
/// (with its full stack and unredacted detail) is logged server-side.
/// </para>
/// </remarks>
public static partial class ToolErrorHandler
{
    /// <summary>Upper bound on a forwarded message; drivers can emit very long text.</summary>
    private const int MaxMessageLength = 2000;

    /// <summary>Maximum number of inner-exception messages appended for context.</summary>
    private const int MaxInnerDepth = 3;

    public static async Task<T> WrapAsync<T>(Func<Task<T>> action, ILogger logger)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (!IsControlFlow(ex))
        {
            throw ToMcpException(ex, logger);
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
            throw ToMcpException(ex, logger);
        }
    }

    /// <summary>
    /// Logs <paramref name="ex"/> in full and returns the client-safe
    /// <see cref="McpException"/> that should be thrown in its place. An
    /// exception that is already an <see cref="McpException"/> is returned
    /// unchanged so a message curated further down the stack is not re-wrapped.
    /// </summary>
    public static McpException ToMcpException(Exception ex, ILogger logger)
    {
        if (ex is McpException mcp)
        {
            return mcp;
        }

        logger.LogError(ex, "Tool execution failed");
        return new McpException(Sanitize(ex), ex);
    }

    /// <summary>
    /// Exceptions that carry protocol meaning rather than a failure, and so must
    /// reach the SDK unmodified.
    /// </summary>
    /// <remarks>
    /// <see cref="InputRequiredException"/> is how a tool asks the client for more
    /// input under the multi round-trip request pattern (MCP 2026-07-28, SEP-2322).
    /// The SDK turns it into an <c>input_required</c> result; converting it to an
    /// <see cref="McpException"/> here would surface every confirmation prompt as a
    /// tool error instead. <see cref="McpException"/> is already in the shape the
    /// SDK propagates verbatim, so it passes through untouched as well.
    /// </remarks>
    public static bool IsControlFlow(Exception ex) =>
        ex is OperationCanceledException or InputRequiredException or UrlElicitationRequiredException or McpException;

    /// <summary>
    /// Builds a client-safe, actionable description of <paramref name="ex"/>.
    /// </summary>
    private static string Sanitize(Exception ex)
    {
        var message = Describe(ex);
        message = Redact(message);
        return message.Length > MaxMessageLength
            ? string.Concat(message.AsSpan(0, MaxMessageLength), "… (truncated; see server logs)")
            : message;
    }

    private static string Describe(Exception ex) => ex switch
    {
        // Domain failures already carry a caller-facing message.
        AccessDeniedException e => e.Message,
        IsolationRewriteFailedException e => $"Data isolation error: {e.Message}",
        QuerySyntaxException e => $"SQL syntax error ({e.Dialect}): {WithInner(e)}",
        QueryParseException e => $"SQL parse error: {WithInner(e)}",
        ReadOnlyConnectionViolationException e => $"Read-only connection: {e.Message}",
        MutationOnReadPathException e => e.Message,
        DestructiveOperationConfirmationRequiredException e => e.Message,
        DestructiveOperationCancelledException e => e.Message,
        WriteAccessConfirmationRequiredException e => e.Message,
        WriteAccessDeclinedException e => e.Message,

        // Lookup and validation.
        KeyNotFoundException e => $"Not found: {e.Message}",
        ArgumentException e => $"Invalid parameters: {DescribeArgument(e)}",
        JsonException e => $"Invalid parameters: the arguments could not be parsed ({e.Message})",
        FormatException e => $"Invalid parameters: {e.Message}",
        NotSupportedException e => $"Not supported: {e.Message}",

        // Transport / timing.
        TimeoutException e => $"Timed out: {WithInner(e)}",
        SocketException e => $"Connection failed: {e.Message} (socket error {e.SocketErrorCode}).",

        // Database drivers. DbException is the provider-agnostic base for both
        // Npgsql and Microsoft.Data.SqlClient, and exposes SqlState since .NET 8.
        DbException e => DescribeDatabase(e),

        _ => WithInner(ex),
    };

    /// <summary>
    /// Describes a driver failure, separating "could not reach the server" from
    /// "the server rejected the statement" because the two need different fixes.
    /// </summary>
    private static string DescribeDatabase(DbException ex)
    {
        // A socket or auth failure underneath the driver means the connection
        // never came up; the SQL, if any, was never sent.
        if (FindInner<SocketException>(ex) is { } socket)
        {
            return $"Connection failed: {ex.Message} (socket error {socket.SocketErrorCode}: {socket.Message}).";
        }

        var builder = new StringBuilder("Database error");
        if (!string.IsNullOrEmpty(ex.SqlState))
        {
            builder.Append(" [").Append(ex.SqlState).Append(']');
        }

        builder.Append(": ").Append(WithInner(ex));

        if (ex.IsTransient)
        {
            builder.Append(" This error is transient; retrying may succeed.");
        }

        return builder.ToString();
    }

    /// <summary>
    /// Adds the offending parameter name when the framework recorded one, so the
    /// model knows which argument to correct.
    /// </summary>
    private static string DescribeArgument(ArgumentException ex) =>
        string.IsNullOrEmpty(ex.ParamName) || ex.Message.Contains(ex.ParamName, StringComparison.Ordinal)
            ? ex.Message
            : $"{ex.Message} (parameter '{ex.ParamName}')";

    /// <summary>
    /// Flattens the inner-exception chain onto the outer message. Drivers
    /// routinely put the actionable cause ("Connection refused", "password
    /// authentication failed") in an inner exception while the outer message says
    /// only "Failed to connect".
    /// </summary>
    private static string WithInner(Exception ex)
    {
        var builder = new StringBuilder(ex.Message);
        var seen = ex.Message;
        var inner = ex.InnerException;

        for (var depth = 0; inner is not null && depth < MaxInnerDepth; depth++, inner = inner.InnerException)
        {
            var text = inner.Message;
            if (string.IsNullOrWhiteSpace(text) || seen.Contains(text, StringComparison.Ordinal))
            {
                continue;
            }

            builder.Append(" -> ").Append(text);
            seen = builder.ToString();
        }

        return builder.ToString();
    }

    private static T? FindInner<T>(Exception ex) where T : Exception
    {
        for (var current = ex.InnerException; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// Strips secrets from a message that is about to cross the wire.
    /// </summary>
    /// <remarks>
    /// Redaction replaces the secret value rather than discarding the whole
    /// message: a blanket "a database error occurred" tells the model nothing,
    /// which is the failure this class exists to fix. Only the credential-bearing
    /// connection-string keywords are masked; host, port, and database names are
    /// diagnostic, not secret, and are supplied by the caller in the first place.
    /// </remarks>
    private static string Redact(string message) => SecretKeywordRegex().Replace(message, "$1=***");

    [GeneratedRegex(
        @"\b(password|pwd|user\s*id|uid|secret|token|api[_\s-]?key|access[_\s-]?key)\s*=\s*(""[^""]*""|'[^']*'|[^;""'\s]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretKeywordRegex();
}
