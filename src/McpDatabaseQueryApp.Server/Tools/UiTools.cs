using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

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
    [McpMeta("ui", JsonValue = "{\"resourceUri\":\"ui://mcp-database-query-app/builder.html\",\"visibility\":[\"model\",\"app\"]}")]
    [Description("Opens the interactive SQL builder UI. Text-mode clients receive an instructional message instead.")]
    public OpenUiResult OpenBuilder(string connectionId)
    {
        return ToolErrorHandler.Wrap(() => new OpenUiResult(
            "ui://mcp-database-query-app/builder.html",
            connectionId,
            "Launching the SQL builder. In a text-only client, use db_tables_list + db_query instead."), _logger);
    }

    [McpServerTool(Name = "ui_results_export_csv", ReadOnly = true)]
    [McpMeta("ui", JsonValue = "{\"visibility\":[\"app\"]}")]
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
        sb.Append(string.Join(',', columns.Select(EscapeCsv))).Append('\n');
        foreach (var row in rows)
        {
            sb.Append(string.Join(',', row.Select(cell => EscapeCsv(cell?.ToString() ?? string.Empty)))).Append('\n');
        }

        return new UiCsvResult(sb.ToString(), rows.Count);
        }, _logger);
    }

    [McpServerTool(Name = "ui_chart", ReadOnly = true)]
    [McpMeta("ui", JsonValue = "{\"resourceUri\":\"ui://mcp-database-query-app/chart.html\",\"visibility\":[\"model\",\"app\"]}")]
    [Description("Opens a Chart.js visualization for the supplied result set. Pass columns and rows from a prior db_query call.")]
    public OpenChartResult OpenChart(
        string connectionId,
        [Description("Chart type: bar, line, timeseries, pie, doughnut. Use timeseries when the X axis is a date/timestamp.")] string chartType,
        [Description("Column names in result-set order. Must align with each row.")] IReadOnlyList<string> columns,
        [Description("Row values. Each row must have the same length as columns.")] IReadOnlyList<IReadOnlyList<object?>> rows,
        [Description("Column name to use as the X axis. Defaults to the first column.")] string? xAxis = null,
        [Description("Column name to use as the Y axis. Defaults to the second column.")] string? yAxis = null)
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
                yAxis);
        }, _logger);
    }

    [McpServerTool(Name = "ui_schema_view", ReadOnly = true)]
    [McpMeta("ui", JsonValue = "{\"resourceUri\":\"ui://mcp-database-query-app/schema-viewer.html\",\"visibility\":[\"model\",\"app\"]}")]
    [Description("Opens an ERD schema viewer showing table relationships and column details.")]
    public OpenUiResult OpenSchemaViewer(string connectionId)
    {
        return ToolErrorHandler.Wrap(() => new OpenUiResult(
            "ui://mcp-database-query-app/schema-viewer.html",
            connectionId,
            "Launching schema viewer. In a text-only client, use db_describe_batch to explore the schema."), _logger);
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(',', StringComparison.Ordinal) || value.Contains('"', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
        {
            return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        }

        return value;
    }
}

public sealed record OpenUiResult(string ResourceUri, string ConnectionId, string TextFallback);

public sealed record OpenChartResult(
    string ResourceUri,
    string ConnectionId,
    string TextFallback,
    string ChartType,
    IReadOnlyList<ChartColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    string? XAxis,
    string? YAxis);

public sealed record ChartColumn(string Name, string? DataType);

public sealed record UiCsvResult(string Csv, int RowCount);
