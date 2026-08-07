using McpDatabaseQueryApp.Apps;
using System.ComponentModel;
using McpDatabaseQueryApp.Core.Results;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Server;

// The MCP Apps extension (io.modelcontextprotocol/apps) is still marked experimental by the
// SDK (MCPEXP003). Suppressed at file scope rather than project-wide so that any *other*
// experimental API introduced elsewhere still fails the build and gets a deliberate decision.
#pragma warning disable MCPEXP003

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class UiTools
{
    private readonly ILogger<UiTools> _logger;

    public UiTools(ILogger<UiTools> logger)
    {
        _logger = logger;
    }

    [McpServerTool(Name = "db_builder_open", ReadOnly = true)]
    [McpAppUi(ResourceUri = AppResources.BuilderUri, Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App])]
    [Description("Opens the interactive SQL builder UI. Text-mode clients receive an instructional message instead.")]
    public OpenUiResult OpenBuilder(string connectionId)
    {
        return ToolErrorHandler.Wrap(() => new OpenUiResult(
            "ui://mcp-database-query-app/builder.html",
            connectionId,
            "Launching the SQL builder. In a text-only client, use db_tables_list + db_query instead."), _logger);
    }

    [McpServerTool(Name = "ui_results_export_csv", ReadOnly = true)]
    [McpAppUi(Visibility = [McpUiToolVisibility.App])]
    [Description("UI-only helper. Produces a CSV-formatted string from a cached result set.")]
    public UiCsvResult ExportCsv(
        [Description("Column names in desired order.")] IReadOnlyList<string> columns,
        [Description("Row values. Each row must have the same length as columns.")] IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        return ToolErrorHandler.Wrap(() =>
        {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(',', columns.Select(CsvResultWriter.Escape))).Append('\n');
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(cell => CsvResultWriter.Escape(cell?.ToString() ?? string.Empty)))).Append('\n');
        }

        return new UiCsvResult(sb.ToString(), rows.Count);
        }, _logger);
    }

    [McpServerTool(Name = "ui_chart", ReadOnly = true)]
    [McpAppUi(ResourceUri = AppResources.ChartUri, Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App])]
    [Description("Opens a Chart.js visualization for the supplied result set. Supports bar, line, area, scatter, timeseries, combo (mixed bar+line), pie and doughnut charts — with multiple series, a secondary Y axis, stacking, dashed/forecast segments and confidence bands. Pass columns and rows from a prior db_query call.")]
    public OpenChartResult OpenChart(
        string connectionId,
        [Description("Chart type: bar, line, area, scatter, timeseries, combo, pie, doughnut. Use timeseries when the X axis is a date/timestamp; combo mixes bar and line series.")] string chartType,
        [Description("Column names in result-set order. Must align with each row.")] IReadOnlyList<string> columns,
        [Description("Row values. Each row must have the same length as columns.")] IReadOnlyList<IReadOnlyList<object?>> rows,
        [Description("Column for the X axis (categories, or the time axis for timeseries). Defaults to the first column.")] string? xAxis = null,
        [Description("Column for a single Y series. Defaults to the first numeric column. Ignored when yAxes or series is set.")] string? yAxis = null,
        [Description("Multiple Y columns to plot as separate series (multi-line or grouped/stacked bars). Ignored when series is set.")] IReadOnlyList<string>? yAxes = null,
        [Description("Full per-series control (column, label, type=line|bar, axis=left|right, dashed, fill, color). Overrides yAxis/yAxes when provided.")] IReadOnlyList<ChartSeriesSpec>? series = null,
        [Description("Stack bars/areas cumulatively instead of drawing them side by side.")] bool stacked = false,
        [Description("Boolean/flag column marking forecast rows; those trailing segments are drawn dashed to distinguish projection from history.")] string? forecastColumn = null,
        [Description("Numeric column holding the lower confidence bound; shaded together with upperBoundColumn as a band behind the first series.")] string? lowerBoundColumn = null,
        [Description("Numeric column holding the upper confidence bound; pair with lowerBoundColumn.")] string? upperBoundColumn = null)
    {
        return ToolErrorHandler.Wrap(() =>
        {
            ArgumentNullException.ThrowIfNull(columns);
            ArgumentNullException.ThrowIfNull(rows);
            var chartColumns = columns.Select(name => new ChartColumn(name, null)).ToList();
            return new OpenChartResult(
                "ui://mcp-database-query-app/chart.html",
                connectionId,
                $"Launching {chartType} chart visualization. In a text-only client, query results are returned as text tables.",
                chartType,
                chartColumns,
                rows,
                rows.Count,
                xAxis,
                yAxis,
                yAxes,
                series,
                stacked,
                forecastColumn,
                lowerBoundColumn,
                upperBoundColumn);
        }, _logger);
    }

    [McpServerTool(Name = "ui_schema_view", ReadOnly = true)]
    [McpAppUi(ResourceUri = AppResources.SchemaViewerUri, Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App])]
    [Description("Opens an ERD schema viewer showing table relationships and column details.")]
    public OpenUiResult OpenSchemaViewer(string connectionId)
    {
        return ToolErrorHandler.Wrap(() => new OpenUiResult(
            "ui://mcp-database-query-app/schema-viewer.html",
            connectionId,
            "Launching schema viewer. In a text-only client, use db_describe_batch to explore the schema."), _logger);
    }

}

public sealed record OpenUiResult(string ResourceUri, string ConnectionId, string TextFallback);

/// <summary>
/// One series in a <c>ui_chart</c> <c>series</c> list. Every field beyond
/// <see cref="Column"/> is optional; the interactive UI exposes the same knobs
/// so the user can tweak whatever the model picked.
/// </summary>
public sealed class ChartSeriesSpec
{
    [Description("Result-set column supplying this series' values.")]
    public required string Column { get; set; }

    [Description("Legend label. Defaults to the column name.")]
    public string? Label { get; set; }

    [Description("Per-series render type for combo charts: line or bar. Defaults to the chart's type.")]
    public string? Type { get; set; }

    [Description("Which Y axis to bind to: left (default) or right (secondary axis).")]
    public string? Axis { get; set; }

    [Description("Draw the whole series with a dashed stroke.")]
    public bool Dashed { get; set; }

    [Description("Fill the area under a line series.")]
    public bool Fill { get; set; }

    [Description("Explicit CSS color (e.g. #4e79a7). Defaults to a palette color.")]
    public string? Color { get; set; }
}

public sealed record OpenChartResult(
    string ResourceUri,
    string ConnectionId,
    string TextFallback,
    string ChartType,
    IReadOnlyList<ChartColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    string? XAxis,
    string? YAxis,
    IReadOnlyList<string>? YAxes,
    IReadOnlyList<ChartSeriesSpec>? Series,
    bool Stacked,
    string? ForecastColumn,
    string? LowerBoundColumn,
    string? UpperBoundColumn);

public sealed record ChartColumn(string Name, string? DataType);

public sealed record UiCsvResult(string Csv, int RowCount);
