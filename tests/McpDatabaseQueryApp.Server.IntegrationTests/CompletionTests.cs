using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

public sealed class CompletionTests
{
    [Fact]
    public async Task Completion_for_providers_resource_returns_database_names()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        await harness.Client.CallToolAsync("db_predefined_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "alpha-db",
                provider = "Postgres",
                host = "localhost",
                database = "alpha",
                username = "u",
                password = "pw",
                sslMode = "Require",
            },
        });

        var completion = await harness.Client.CompleteAsync(
            new ResourceTemplateReference { Uri = "mcpdb://databases/{name}" },
            argumentName: "name",
            argumentValue: string.Empty);

        completion.Completion.Values.Should().Contain("alpha-db");
    }

    [Fact]
    public async Task Completion_for_scripts_resource_returns_script_names()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "clean",
                sqlText = "SELECT 1;",
            },
        });

        var completion = await harness.Client.CompleteAsync(
            new ResourceTemplateReference { Uri = "mcpdb://scripts/{name}" },
            argumentName: "name",
            argumentValue: "c");

        completion.Completion.Values.Should().Contain("clean");
    }

    [Fact]
    public async Task Completion_prompt_provider_argument_lists_database_kinds()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var completion = await harness.Client.CompleteAsync(
            new PromptReference { Name = "migrate-script" },
            argumentName: "provider",
            argumentValue: string.Empty);

        completion.Completion.Values.Should().Contain(["Postgres", "SqlServer"]);
    }
}
