using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol;

namespace McpDatabaseQueryApp.Server;

/// <summary>
/// The single <see cref="JsonSerializerOptions"/> instance used for MCP tool
/// schema generation and for anything else that has to serialize a tool result
/// with exactly the same contract — currently the HTTP result links, whose
/// payload must be byte-for-byte the JSON a client would have received inline.
/// </summary>
public static class McpDatabaseQueryAppJsonOptions
{
    /// <summary>
    /// Per the MCP SDK docs, the correct way to make tool schema generation see
    /// user DTOs is to pass a preconfigured <see cref="JsonSerializerOptions"/>
    /// into each <c>WithTools&lt;T&gt;(options)</c> call. The SDK's own resolver
    /// goes first so protocol types (including experimental properties) keep
    /// their SDK contract; the source-gen context covers the McpDatabaseQueryApp
    /// DTOs; the reflection resolver is kept as a trailing fallback for
    /// <c>object?</c> query-row values that can't be described statically (see
    /// <c>McpDatabaseQueryAppJsonContext</c>).
    /// </summary>
    public static JsonSerializerOptions Tool { get; } = new()
    {
        TypeInfoResolverChain =
        {
            McpJsonUtilities.DefaultOptions.TypeInfoResolver!,
            McpDatabaseQueryAppJsonContext.Default,
            new DefaultJsonTypeInfoResolver(),
        },
    };
}
