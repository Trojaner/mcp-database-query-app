using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// The MCP Apps extension (io.modelcontextprotocol/apps) is still marked experimental by the
// SDK (MCPEXP003). Suppressed at file scope rather than project-wide so that any *other*
// experimental API introduced elsewhere still fails the build and gets a deliberate decision.
#pragma warning disable MCPEXP003

namespace McpDatabaseQueryApp.Apps;

[McpServerResourceType]
public sealed class AppResources
{
    public const string ResultsUri = "ui://mcp-database-query-app/results.html";
    public const string BuilderUri = "ui://mcp-database-query-app/builder.html";
    public const string ChartUri = "ui://mcp-database-query-app/chart.html";
    public const string SchemaViewerUri = "ui://mcp-database-query-app/schema-viewer.html";
    public const string MimeType = McpApps.HtmlMimeType;

    private static readonly Lazy<string> ResultsHtml = new(() => LoadEmbedded("McpDatabaseQueryApp.Apps.ui.results.html"));
    private static readonly Lazy<string> BuilderHtml = new(() => LoadEmbedded("McpDatabaseQueryApp.Apps.ui.builder.html"));
    private static readonly Lazy<string> ChartHtml = new(() => LoadEmbedded("McpDatabaseQueryApp.Apps.ui.chart.html"));
    private static readonly Lazy<string> SchemaViewerHtml = new(() => LoadEmbedded("McpDatabaseQueryApp.Apps.ui.schema-viewer.html"));

    [McpServerResource(UriTemplate = ResultsUri, Name = "results_grid", Title = "MCP Database Query App results grid", MimeType = MimeType)]
    [Description("Inline sortable/filterable grid for query results.")]
    public TextResourceContents Results() => new()
    {
        Uri = ResultsUri,
        MimeType = MimeType,
        Text = ResultsHtml.Value,
        Meta = BuildUiMeta(prefersBorder: true),
    };

    [McpServerResource(UriTemplate = BuilderUri, Name = "sql_builder", Title = "MCP Database Query App SQL builder", MimeType = MimeType)]
    [Description("Interactive SQL builder UI that posts db_query calls back through the host.")]
    public TextResourceContents Builder() => new()
    {
        Uri = BuilderUri,
        MimeType = MimeType,
        Text = BuilderHtml.Value,
        Meta = BuildUiMeta(prefersBorder: true),
    };

    [McpServerResource(UriTemplate = ChartUri, Name = "chart_viewer", Title = "MCP Database Query App chart viewer", MimeType = MimeType)]
    [Description("Chart.js visualization for query results supporting bar, line, area, scatter, timeseries, combo (bar+line), pie and doughnut charts — with multiple series, dual Y axes, stacking, dashed forecast segments and confidence bands.")]
    public TextResourceContents Chart() => new()
    {
        Uri = ChartUri,
        MimeType = MimeType,
        Text = ChartHtml.Value,
        Meta = BuildUiMeta(prefersBorder: true, resourceDomains: new[] { "https://cdn.jsdelivr.net" }),
    };

    [McpServerResource(UriTemplate = SchemaViewerUri, Name = "schema_viewer", Title = "MCP Database Query App schema viewer", MimeType = MimeType)]
    [Description("ERD schema viewer showing table relationships and column details.")]
    public TextResourceContents SchemaViewer() => new()
    {
        Uri = SchemaViewerUri,
        MimeType = MimeType,
        Text = SchemaViewerHtml.Value,
        Meta = BuildUiMeta(prefersBorder: true),
    };

    public static string GetResultsHtml() => ResultsHtml.Value;

    public static string GetBuilderHtml() => BuilderHtml.Value;

    public static string GetChartHtml() => ChartHtml.Value;

    public static string GetSchemaViewerHtml() => SchemaViewerHtml.Value;

    /// <summary>
    /// Builds the <c>_meta.ui</c> block carried on a UI resource's contents.
    /// </summary>
    /// <remarks>
    /// Uses the typed <see cref="McpUiResourceMeta"/> model from the MCP Apps extension rather
    /// than a hand-built <see cref="JsonObject"/>, so the property names and shape come from
    /// the SDK's own contract instead of string literals that drift silently when the Apps
    /// spec moves. Serialized with <see cref="McpApps.SerializerOptions"/> to get the casing
    /// and null-handling the extension expects.
    /// </remarks>
    private static JsonObject BuildUiMeta(bool prefersBorder, IReadOnlyList<string>? resourceDomains = null)
    {
        var meta = new McpUiResourceMeta
        {
            PrefersBorder = prefersBorder,
        };

        if (resourceDomains is { Count: > 0 })
        {
            meta.Csp = new McpUiResourceCsp
            {
                ResourceDomains = [.. resourceDomains],
            };
        }

        // Resolve the contract up front and serialize through JsonTypeInfo rather than the
        // (TValue, JsonSerializerOptions) overload: that overload is annotated
        // RequiresUnreferencedCode/RequiresDynamicCode and would break this project's
        // IsAotCompatible guarantee.
        var typeInfo = (JsonTypeInfo<McpUiResourceMeta>)McpApps.SerializerOptions.GetTypeInfo(typeof(McpUiResourceMeta));
        var ui = JsonSerializer.SerializeToNode(meta, typeInfo)
            ?? throw new InvalidOperationException("Failed to serialize MCP App UI resource metadata.");

        return new JsonObject
        {
            ["ui"] = ui,
        };
    }

    private static string LoadEmbedded(string name)
    {
        var assembly = typeof(AppResources).Assembly;
        using var stream = assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return Fallback(name);
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Fallback(string name) =>
        "<!doctype html><html><body><p>MCP Database Query App UI bundle '" + name + "' was not built. Run `dotnet build` with Node.js available to produce it.</p></body></html>";
}
