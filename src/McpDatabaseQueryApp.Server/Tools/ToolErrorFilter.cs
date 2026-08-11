using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tools;

/// <summary>
/// Server-wide safety net that gives every failed request a real message,
/// including the failures no handler body can catch.
/// </summary>
/// <remarks>
/// <para>
/// The SDK only forwards an exception's message when the exception is an
/// <c>McpException</c>. Everything else collapses to
/// <c>"An error occurred invoking '&lt;tool&gt;'."</c> for tool calls and a bare
/// <c>"An error occurred."</c> JSON-RPC error for resources, prompts, and
/// completions — so a <c>KeyNotFoundException</c> naming the missing database
/// reaches the caller as no information at all.
/// </para>
/// <para>
/// <see cref="ToolErrorHandler"/> can only cover code inside a handler body, which
/// leaves two gaps these filters close. Argument binding runs before the body:
/// the SDK deserializes and coerces <c>arguments</c> into the method's parameters
/// first, so a missing required argument or a wrong JSON type throws outside every
/// <c>Wrap</c> call. And the resource and prompt handlers are not wrapped at all.
/// </para>
/// <para>
/// Handlers that already threw an <c>McpException</c> pass through untouched, so a
/// message curated further down the stack is never re-wrapped.
/// </para>
/// </remarks>
public static class ToolErrorFilter
{
    /// <summary>
    /// Adds the catch-all error-reporting filters to the server pipeline.
    /// </summary>
    public static IMcpServerBuilder WithDetailedErrorReporting(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithRequestFilters(filters => filters
            .AddCallToolFilter(Filter<CallToolRequestParams, CallToolResult>())
            .AddReadResourceFilter(Filter<ReadResourceRequestParams, ReadResourceResult>())
            .AddGetPromptFilter(Filter<GetPromptRequestParams, GetPromptResult>())
            .AddCompleteFilter(Filter<CompleteRequestParams, CompleteResult>())
            .AddListToolsFilter(Filter<ListToolsRequestParams, ListToolsResult>())
            .AddListResourcesFilter(Filter<ListResourcesRequestParams, ListResourcesResult>())
            .AddListPromptsFilter(Filter<ListPromptsRequestParams, ListPromptsResult>()));
    }

    private static McpRequestFilter<TParams, TResult> Filter<TParams, TResult>() =>
        next => async (context, cancellationToken) =>
        {
            try
            {
                return await next(context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (!ToolErrorHandler.IsControlFlow(ex))
            {
                var logger = context.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(ToolErrorFilter))
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
                throw ToolErrorHandler.ToMcpException(ex, logger);
            }
        };
}
