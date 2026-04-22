using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Elicitation;

public interface IElicitationGateway
{
    Task<bool> ConfirmAsync(McpServer server, string message, CancellationToken cancellationToken);

    Task<string?> AskTextAsync(McpServer server, string fieldName, string description, string message, CancellationToken cancellationToken);

    bool ClientSupportsForm(McpServer server);

    bool ClientSupportsUrl(McpServer server);
}

public sealed class ElicitationGateway : IElicitationGateway
{
    public bool ClientSupportsForm(McpServer server) =>
        server.ClientCapabilities?.Elicitation is not null;

    public bool ClientSupportsUrl(McpServer server) =>
        server.ClientCapabilities?.Elicitation?.Url is not null;

    public async Task<bool> ConfirmAsync(McpServer server, string message, CancellationToken cancellationToken)
    {
        if (!ClientSupportsForm(server))
        {
            return false;
        }

        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
            {
                ["confirm"] = new ElicitRequestParams.BooleanSchema
                {
                    Description = "Set to true to proceed with this operation.",
                    Default = false,
                },
            },
        };

        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Mode = "form",
            Message = message,
            RequestedSchema = schema,
        }, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(result.Action, "accept", StringComparison.Ordinal))
        {
            return false;
        }

        if (result.Content is { } content
            && content.TryGetValue("confirm", out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            return true;
        }

        return false;
    }

    public async Task<string?> AskTextAsync(
        McpServer server,
        string fieldName,
        string description,
        string message,
        CancellationToken cancellationToken)
    {
        if (!ClientSupportsForm(server))
        {
            return null;
        }

        var schema = new ElicitRequestParams.RequestSchema
        {
            Properties = new Dictionary<string, ElicitRequestParams.PrimitiveSchemaDefinition>(StringComparer.Ordinal)
            {
                [fieldName] = new ElicitRequestParams.StringSchema
                {
                    Description = description,
                },
            },
        };

        var result = await server.ElicitAsync(new ElicitRequestParams
        {
            Mode = "form",
            Message = message,
            RequestedSchema = schema,
        }, cancellationToken).ConfigureAwait(false);

        if (!string.Equals(result.Action, "accept", StringComparison.Ordinal) || result.Content is null)
        {
            return null;
        }

        return result.Content.TryGetValue(fieldName, out var element) && element.ValueKind == System.Text.Json.JsonValueKind.String
            ? element.GetString()
            : null;
    }
}
