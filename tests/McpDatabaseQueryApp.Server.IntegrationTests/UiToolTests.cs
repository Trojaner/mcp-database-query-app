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
    {
        var structured = result.StructuredContent
            ?? JsonDocument.Parse(((ModelContextProtocol.Protocol.TextContentBlock)result.Content[0]).Text!).RootElement;
        return structured.GetProperty(fieldName).GetString()!;
    }
}
