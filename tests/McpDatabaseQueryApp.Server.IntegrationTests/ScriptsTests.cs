using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

public sealed class ScriptsTests
{
    private static string UnwrappedText(CallToolResult result)
    {
        var block = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        return block?.Text ?? string.Empty;
    }

    [Fact]
    public async Task Create_list_get_delete_script()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var create = await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "count-users",
                description = "Counts active users",
                sqlText = "SELECT COUNT(*) FROM users WHERE active = true;",
                destructive = false,
                tags = new[] { "read" },
            },
        });
        create.IsError.Should().NotBe(true);

        var list = await harness.Client.CallToolAsync("scripts_list", new Dictionary<string, object?>
        {
            ["cursor"] = null,
            ["filter"] = null,
        });
        list.IsError.Should().NotBe(true);
        UnwrappedText(list).Should().Contain("count-users");

        var get = await harness.Client.CallToolAsync("scripts_get", new Dictionary<string, object?> { ["nameOrId"] = "count-users" });
        get.IsError.Should().NotBe(true);
        UnwrappedText(get).Should().Contain("SELECT COUNT");

        var deleted = await harness.Client.CallToolAsync("scripts_delete", new Dictionary<string, object?>
        {
            ["nameOrId"] = "count-users",
            ["confirm"] = true,
        });
        deleted.IsError.Should().NotBe(true);
        UnwrappedText(deleted).Should().Contain("\"deleted\":true");
    }

    [Fact]
    public async Task Script_without_sql_is_rejected()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var create = await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "empty",
                sqlText = "",
            },
        });
        create.IsError.Should().BeTrue();
    }

    [Fact]
    public async Task Destructive_detection_marks_script_automatically()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var create = await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "drop-audit",
                sqlText = "DROP TABLE audit;",
            },
        });
        create.IsError.Should().NotBe(true);

        var get = await harness.Client.CallToolAsync("scripts_get", new Dictionary<string, object?> { ["nameOrId"] = "drop-audit" });
        UnwrappedText(get).Should().Contain("\"destructive\":true");
    }

    [Fact]
    public async Task Scripts_list_paginates()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        for (var i = 0; i < 5; i++)
        {
            await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
            {
                ["args"] = new
                {
                    name = $"s{i:D2}",
                    sqlText = $"SELECT {i};",
                },
            });
        }

        var first = await harness.Client.CallToolAsync("scripts_list", new Dictionary<string, object?>
        {
            ["cursor"] = null,
            ["filter"] = null,
        });
        first.IsError.Should().NotBe(true);
        var text = UnwrappedText(first);
        text.Should().Contain("s00").And.Contain("s04");
    }

    [Fact]
    public async Task Script_resource_exposes_saved_scripts()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "first",
                sqlText = "SELECT 1;",
            },
        });

        var resource = await harness.Client.ReadResourceAsync("mcpdb://scripts");
        var text = (resource.Contents[0] as TextResourceContents)!.Text;
        text.Should().Contain("first");
    }
}
