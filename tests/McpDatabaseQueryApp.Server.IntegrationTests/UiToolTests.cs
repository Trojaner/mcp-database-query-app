using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

public sealed class UiToolTests
{
    [Fact]
    public async Task Ui_export_csv_escapes_special_characters()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.CallToolAsync("ui_results_export_csv", new Dictionary<string, object?>
        {
            ["columns"] = new[] { "name", "notes" },
            ["rows"] = new[]
            {
                new object?[] { "alice", "hello, world" },
                new object?[] { "bob", "quote \"yes\"" },
                new object?[] { "eve", "multi\nline" },
            },
        });

        result.IsError.Should().NotBe(true);
        var csv = ExtractStructuredField(result, "csv");
        csv.Should().Contain("\"hello, world\"");
        csv.Should().Contain("\"quote \"\"yes\"\"\"");
        csv.Should().Contain("\"multi\nline\"");
    }

    [Fact]
    public async Task Ui_chart_echoes_advanced_spec_into_structured_result()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.CallToolAsync("ui_chart", new Dictionary<string, object?>
        {
            ["connectionId"] = "conn_abc123",
            ["chartType"] = "combo",
            ["columns"] = new[] { "month", "revenue", "orders" },
            ["rows"] = new[]
            {
                new object?[] { "Jan", 100, 12 },
                new object?[] { "Feb", 140, 18 },
            },
            ["stacked"] = true,
            ["series"] = new[]
            {
                new Dictionary<string, object?> { ["column"] = "revenue", ["type"] = "bar", ["axis"] = "left" },
                new Dictionary<string, object?> { ["column"] = "orders", ["type"] = "line", ["axis"] = "right", ["dashed"] = true },
            },
        });

        result.IsError.Should().NotBe(true);
        var structured = GetStructured(result);
        structured.GetProperty("resourceUri").GetString().Should().Be("ui://mcp-database-query-app/chart.html");
        structured.GetProperty("chartType").GetString().Should().Be("combo");
        structured.GetProperty("stacked").GetBoolean().Should().BeTrue();
        structured.GetProperty("rowCount").GetInt32().Should().Be(2);

        var series = structured.GetProperty("series");
        series.GetArrayLength().Should().Be(2);
        series[0].GetProperty("column").GetString().Should().Be("revenue");
        series[0].GetProperty("type").GetString().Should().Be("bar");
        series[1].GetProperty("column").GetString().Should().Be("orders");
        series[1].GetProperty("axis").GetString().Should().Be("right");
        series[1].GetProperty("dashed").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Ui_chart_supports_basic_single_series_call()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.CallToolAsync("ui_chart", new Dictionary<string, object?>
        {
            ["connectionId"] = "conn_abc123",
            ["chartType"] = "bar",
            ["columns"] = new[] { "category", "total" },
            ["rows"] = new[] { new object?[] { "a", 3 }, new object?[] { "b", 7 } },
            ["xAxis"] = "category",
            ["yAxis"] = "total",
        });

        result.IsError.Should().NotBe(true);
        var structured = GetStructured(result);
        structured.GetProperty("chartType").GetString().Should().Be("bar");
        structured.GetProperty("xAxis").GetString().Should().Be("category");
        structured.GetProperty("yAxis").GetString().Should().Be("total");
        structured.GetProperty("stacked").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Ui_builder_open_returns_resource_uri()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.CallToolAsync("db_builder_open", new Dictionary<string, object?>
        {
            ["connectionId"] = "conn_abc123",
        });

        result.IsError.Should().NotBe(true);
        var uri = ExtractStructuredField(result, "resourceUri");
        uri.Should().Be("ui://mcp-database-query-app/builder.html");
    }

    private static string ExtractStructuredField(ModelContextProtocol.Protocol.CallToolResult result, string fieldName)
        => GetStructured(result).GetProperty(fieldName).GetString()!;

    private static JsonElement GetStructured(ModelContextProtocol.Protocol.CallToolResult result)
        => result.StructuredContent
            ?? JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text!).RootElement;
}
