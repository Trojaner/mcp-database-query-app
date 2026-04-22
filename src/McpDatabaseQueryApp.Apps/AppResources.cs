using System.ComponentModel;
using System.Reflection;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Apps;

[McpServerResourceType]
public sealed class AppResources
{
    public const string ResultsUri = "ui://mcp-database-query-app/results.html";
    public const string BuilderUri = "ui://mcp-database-query-app/builder.html";
    public const string ChartUri = "ui://mcp-database-query-app/chart.html";
    public const string SchemaViewerUri = "ui://mcp-database-query-app/schema-viewer.html";
    public const string MimeType = "text/html;profile=mcp-app";

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
    [Description("Chart.js visualization for query results supporting bar, line, pie, and doughnut chart types.")]
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

    private static JsonObject BuildUiMeta(bool prefersBorder, IReadOnlyList<string>? resourceDomains = null)
    {
        var ui = new JsonObject
        {
            ["prefersBorder"] = prefersBorder,
        };

        if (resourceDomains is { Count: > 0 })
        {
            var domains = new JsonArray();
            foreach (var domain in resourceDomains)
            {
                domains.Add(domain);
            }

            ui["csp"] = new JsonObject
            {
                ["resourceDomains"] = domains,
            };
        }

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
