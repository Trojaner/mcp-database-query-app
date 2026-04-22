using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

public sealed class PredefinedDbLifecycleTests
{
    private static string UnwrappedText(CallToolResult result)
    {
        var block = result.Content.OfType<TextContentBlock>().FirstOrDefault();
        return block?.Text ?? string.Empty;
    }

    [Fact]
    public async Task Create_and_get_predefined_database_redacts_password()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var args = new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "analytics",
                provider = "Postgres",
                host = "db.example.com",
                port = 5432,
                database = "analytics",
                username = "reader",
                password = "topsecret",
                sslMode = "Require",
                readOnly = true,
                tags = new[] { "prod" },
            },
        };

        var createResult = await harness.Client.CallToolAsync("db_predefined_create", args);
        createResult.IsError.Should().NotBe(true);
        var createJson = JsonSerializer.Serialize(createResult);
        createJson.Should().NotContain("topsecret", because: "the password must never be echoed back");

        var getResult = await harness.Client.CallToolAsync(
            "db_predefined_get",
            new Dictionary<string, object?> { ["nameOrId"] = "analytics" });
        getResult.IsError.Should().NotBe(true);
        var getJson = JsonSerializer.Serialize(getResult);
        getJson.Should().NotContain("topsecret");
        getJson.Should().Contain("analytics");
    }

    [Fact]
    public async Task Predefined_database_appears_in_list_and_resource()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        await harness.Client.CallToolAsync("db_predefined_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "shop",
                provider = "Postgres",
                host = "localhost",
                database = "shop",
                username = "u",
                password = "secret1",
                sslMode = "Require",
            },
        });

        var list = await harness.Client.CallToolAsync("db_list_predefined", new Dictionary<string, object?>
        {
            ["cursor"] = null,
            ["filter"] = null,
        });
        list.IsError.Should().NotBe(true);
        var listText = UnwrappedText(list);
        listText.Should().Contain("shop");
        listText.Should().NotContain("secret1");

        var resource = await harness.Client.ReadResourceAsync("mcpdb://databases");
        var text = (resource.Contents[0] as TextResourceContents)!.Text;
        text.Should().Contain("shop");
        text.Should().NotContain("secret1");
    }

    [Fact]
    public async Task Predefined_delete_without_confirm_triggers_elicitation_decline()
    {
        var handlers = new ModelContextProtocol.Client.McpClientHandlers
        {
            ElicitationHandler = (request, ct) => ValueTask.FromResult(new ElicitResult
            {
                Action = "decline",
            }),
        };

        await using var harness = await InProcessServerHarness.StartAsync(clientHandlers: handlers);

        await harness.Client.CallToolAsync("db_predefined_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "temp",
                provider = "Postgres",
                host = "localhost",
                database = "temp",
                username = "u",
                password = "pw",
                sslMode = "Require",
            },
        });

        var deleted = await harness.Client.CallToolAsync("db_predefined_delete", new Dictionary<string, object?>
        {
            ["nameOrId"] = "temp",
            ["confirm"] = false,
        });
        deleted.IsError.Should().NotBe(true);
        UnwrappedText(deleted).Should().Contain("\"deleted\":false");

        var get = await harness.Client.CallToolAsync("db_predefined_get", new Dictionary<string, object?> { ["nameOrId"] = "temp" });
        JsonSerializer.Serialize(get).Should().Contain("temp", because: "the entry must still exist after decline");
    }

    [Fact]
    public async Task Predefined_delete_with_confirm_bypasses_elicitation()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        await harness.Client.CallToolAsync("db_predefined_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "transient",
                provider = "Postgres",
                host = "localhost",
                database = "t",
                username = "u",
                password = "pw",
                sslMode = "Require",
            },
        });

        var deleted = await harness.Client.CallToolAsync("db_predefined_delete", new Dictionary<string, object?>
        {
            ["nameOrId"] = "transient",
            ["confirm"] = true,
        });
        UnwrappedText(deleted).Should().Contain("\"deleted\":true");
    }
}
