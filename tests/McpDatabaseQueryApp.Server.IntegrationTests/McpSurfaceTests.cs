using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

public sealed class McpSurfaceTests
{
    [Fact]
    public async Task Initialize_succeeds_and_negotiates_capabilities()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        harness.Client.ServerInfo.Name.Should().Be("McpDatabaseQueryApp.Test");
        harness.Client.ServerCapabilities.Tools.Should().NotBeNull();
        harness.Client.ServerCapabilities.Resources.Should().NotBeNull();
        harness.Client.ServerCapabilities.Prompts.Should().NotBeNull();
        harness.Client.ServerCapabilities.Completions.Should().NotBeNull();
    }

    [Fact]
    public async Task ListTools_returns_connection_query_schema_and_ui_tools()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var tools = await harness.Client.ListToolsAsync();

        var names = tools.Select(t => t.Name).ToList();
        names.Should().Contain([
            "db_list_predefined",
            "db_connect",
            "db_disconnect",
            "db_list_connections",
            "db_ping",
            "db_predefined_create",
            "db_predefined_get",
            "db_predefined_update",
            "db_predefined_delete",
            "db_query",
            "db_query_next_page",
            "db_execute",
            "db_explain",
            "db_schemas_list",
            "db_tables_list",
            "db_table_describe",
            "db_roles_list",
            "db_databases_list",
            "scripts_list",
            "scripts_get",
            "scripts_create",
            "scripts_update",
            "scripts_delete",
            "scripts_run",
            "db_builder_open",
            "ui_results_export_csv",
        ]);
    }

    [Fact]
    public async Task Tools_serialize_without_exposing_password_on_output_schemas()
    {
        // `db_predefined_create` legitimately accepts a password as an input parameter that the server encrypts.
        // Every other tool must be free of password fields in its schema.
        // db_predefined_create and db_connect legitimately take a password as input that the server encrypts.
        // Every other tool must be free of password fields in its schema.
        var allowedInputPasswordTools = new HashSet<string>(StringComparer.Ordinal)
        {
            "db_predefined_create",
            "db_connect",
        };

        await using var harness = await InProcessServerHarness.StartAsync();
        var tools = await harness.Client.ListToolsAsync();
        foreach (var tool in tools)
        {
            if (allowedInputPasswordTools.Contains(tool.Name))
            {
                continue;
            }

            var json = JsonSerializer.Serialize(tool.ProtocolTool);
            json.Should().NotContain("password", because: $"tool '{tool.Name}' must not expose a password field");
        }
    }

    [Fact]
    public async Task ListResources_exposes_mcpdb_and_ui_resources()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var resources = await harness.Client.ListResourcesAsync();

        resources.Should().Contain(r => r.Uri == "mcpdb://providers");
        resources.Should().Contain(r => r.Uri == "mcpdb://databases");
        resources.Should().Contain(r => r.Uri == "mcpdb://connections");
        resources.Should().Contain(r => r.Uri == "mcpdb://scripts");
        resources.Should().Contain(r => r.Uri == "ui://mcp-database-query-app/results.html");
        resources.Should().Contain(r => r.Uri == "ui://mcp-database-query-app/builder.html");
    }

    [Fact]
    public async Task ReadResource_returns_providers_json()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.ReadResourceAsync("mcpdb://providers");

        result.Contents.Should().ContainSingle();
        var content = result.Contents[0] as TextResourceContents;
        content.Should().NotBeNull();
        content!.Text.Should().Contain("Postgres").And.Contain("SqlServer");
    }

    [Fact]
    public async Task ReadResource_returns_results_html_from_apps_bundle()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.ReadResourceAsync("ui://mcp-database-query-app/results.html");

        var content = result.Contents[0] as TextResourceContents;
        content.Should().NotBeNull();
        content!.MimeType.Should().Contain("mcp-app");
        content.Text.Should().Contain("<!DOCTYPE html>");
        content.Text.Should().Contain("MCP Database Query App Results");
    }

    [Fact]
    public async Task ReadResource_returns_builder_html_from_apps_bundle()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.ReadResourceAsync("ui://mcp-database-query-app/builder.html");

        var content = result.Contents[0] as TextResourceContents;
        content!.Text.Should().Contain("MCP Database Query App SQL Builder");
    }

    [Fact]
    public async Task ListPrompts_returns_seed_prompts()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var prompts = await harness.Client.ListPromptsAsync();

        prompts.Select(p => p.Name).Should().Contain([
            "explore-database",
            "safe-write",
            "migrate-script",
            "explain-slow-query",
        ]);
    }

    [Fact]
    public async Task GetPrompt_returns_user_message_with_substitution()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.GetPromptAsync(
            "explore-database",
            new Dictionary<string, object?> { ["connectionId"] = "conn_abcd12" });

        result.Messages.Should().NotBeEmpty();
        result.Messages[0].Role.Should().Be(Role.User);
        var text = result.Messages[0].Content as TextContentBlock;
        text!.Text.Should().Contain("conn_abcd12");
    }
}
