using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Elicitation;

/// <summary>
/// Asks the end user a question in the middle of a tool call.
/// </summary>
/// <remarks>
/// <para>
/// Two protocol paths are supported and chosen per request:
/// </para>
/// <list type="bullet">
/// <item>
/// <description>
/// <b>Multi round-trip requests (MRTR)</b> — the mechanism introduced by MCP
/// 2026-07-28 (SEP-2322). The tool throws <see cref="InputRequiredException"/>;
/// the SDK converts it into an <c>input_required</c> result; the client collects
/// the answer and <b>re-issues the same tool call</b> with the answer attached in
/// <see cref="RequestParams.InputResponses"/>. This is the only path that works on
/// stateless Streamable HTTP, which 2026-07-28 mandates (no sessions, no
/// server-initiated <c>elicitation/create</c>).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Legacy server-initiated elicitation</b> — <see cref="McpServer.ElicitAsync"/>,
/// used only for down-level clients negotiated onto an earlier revision over STDIO
/// or stateful HTTP, where a server-to-client request channel still exists.
/// </description>
/// </item>
/// </list>
/// <para>
/// <see cref="McpServer.IsMrtrSupported"/> selects between them, so both current and
/// older clients keep working without the caller branching.
/// </para>
/// <para>
/// <b>Callers must be replay-safe.</b> Under MRTR a tool body runs more than once for
/// a single logical invocation: once up to the point it asks, and again from the top
/// once the answer is in. Do not perform side effects before the question is answered.
/// </para>
/// </remarks>
public interface IElicitationGateway
{
    /// <summary>Asks the user to confirm an operation.</summary>
    /// <param name="inputKey">
    /// Stable identifier for this question within the tool call. It keys the entry in
    /// <see cref="InputRequiredResult.InputRequests"/> and the matching answer in
    /// <see cref="RequestParams.InputResponses"/>, so a tool that asks two different
    /// questions must use two different keys.
    /// </param>
    Task<bool> ConfirmAsync(
        RequestContext<CallToolRequestParams> context,
        string inputKey,
        string message,
        CancellationToken cancellationToken);

    /// <summary>Asks the user for a single free-text value.</summary>
    /// <param name="inputKey">See <see cref="ConfirmAsync"/>.</param>
    Task<string?> AskTextAsync(
        RequestContext<CallToolRequestParams> context,
        string inputKey,
        string fieldName,
        string description,
        string message,
        CancellationToken cancellationToken);

    /// <summary>
    /// True when the client can answer a question at all, by either path. Use this to
    /// decide whether to ask or to fail closed with an explanatory message.
    /// </summary>
    bool CanElicit(RequestContext<CallToolRequestParams> context);

    bool ClientSupportsForm(McpServer server);

    bool ClientSupportsUrl(McpServer server);
}

public sealed class ElicitationGateway : IElicitationGateway
{
    public bool ClientSupportsForm(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.ClientCapabilities?.Elicitation is not null;
    }

    public bool ClientSupportsUrl(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.ClientCapabilities?.Elicitation?.Url is not null;
    }

    public bool CanElicit(RequestContext<CallToolRequestParams> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Server.IsMrtrSupported || ClientSupportsForm(context.Server);
    }

    public async Task<bool> ConfirmAsync(
        RequestContext<CallToolRequestParams> context,
        string inputKey,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputKey);

        var request = new ElicitRequestParams
        {
            Mode = "form",
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    ["confirm"] = new ElicitRequestParams.BooleanSchema
                    {
                        Description = "Set to true to proceed with this operation.",
                        Default = false,
                    },
                },
            },
        };

        var result = await AskAsync(context, inputKey, request, cancellationToken).ConfigureAwait(false);
        if (result is null || !IsAccepted(result))
        {
            return false;
        }

        return result.Content is { } content
            && content.TryGetValue("confirm", out var value)
            && value.ValueKind == JsonValueKind.True;
    }

    public async Task<string?> AskTextAsync(
        RequestContext<CallToolRequestParams> context,
        string inputKey,
        string fieldName,
        string description,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(inputKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(fieldName);

        var request = new ElicitRequestParams
        {
            Mode = "form",
            Message = message,
            RequestedSchema = new ElicitRequestParams.RequestSchema
            {
                Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
                {
                    [fieldName] = new ElicitRequestParams.StringSchema
                    {
                        Description = description,
                    },
                },
            },
        };

        var result = await AskAsync(context, inputKey, request, cancellationToken).ConfigureAwait(false);
        if (result is null || !IsAccepted(result) || result.Content is null)
        {
            return null;
        }

        return result.Content.TryGetValue(fieldName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }

    /// <summary>
    /// Resolves a single question, returning <c>null</c> when the client cannot be asked.
    /// </summary>
    /// <remarks>
    /// Under MRTR this either returns the replayed answer or throws
    /// <see cref="InputRequiredException"/> — it never returns normally on the first pass.
    /// </remarks>
    private async Task<ElicitResult?> AskAsync(
        RequestContext<CallToolRequestParams> context,
        string inputKey,
        ElicitRequestParams request,
        CancellationToken cancellationToken)
    {
        // The client is replaying the call with the answer attached.
        if (TryReadReplayedAnswer(context, inputKey, out var replayed))
        {
            return replayed;
        }

        if (context.Server.IsMrtrSupported)
        {
            throw new InputRequiredException(
                new Dictionary<string, InputRequest>(StringComparer.Ordinal)
                {
                    [inputKey] = InputRequest.ForElicitation(request),
                },
                requestState: inputKey);
        }

        // Down-level client: server-initiated elicitation still has a channel back.
        if (!ClientSupportsForm(context.Server))
        {
            return null;
        }

        return await context.Server.ElicitAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadReplayedAnswer(
        RequestContext<CallToolRequestParams> context,
        string inputKey,
        out ElicitResult? result)
    {
        result = null;

        if (context.Params?.InputResponses is not { } responses
            || !responses.TryGetValue(inputKey, out var response)
            || response is null)
        {
            return false;
        }

        result = response.Deserialize(InputResponse.ElicitResultJsonTypeInfo);
        return true;
    }

    private static bool IsAccepted(ElicitResult result) =>
        string.Equals(result.Action, "accept", StringComparison.Ordinal);
}
