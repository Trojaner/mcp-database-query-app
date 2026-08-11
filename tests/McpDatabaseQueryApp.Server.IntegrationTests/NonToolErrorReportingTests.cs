using FluentAssertions;
using ModelContextProtocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// Resources and prompts fail through <c>JsonRpcError</c> rather than a
/// <c>CallToolResult</c>, but the SDK applies the same rule: only an
/// <c>McpException</c> keeps its message. Without the server-wide filters a
/// missing database surfaces as the bare string "An error occurred."
/// </summary>
public sealed class NonToolErrorReportingTests
{
    [Fact]
    public async Task Missing_resource_target_reports_what_was_not_found()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var act = async () => await harness.Client.ReadResourceAsync("mcpdb://databases/ghost");

        var ex = await act.Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("ghost").And.Contain("not found");
    }

    [Fact]
    public async Task Unknown_resource_uri_is_reported_by_the_sdk()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var act = async () => await harness.Client.ReadResourceAsync("mcpdb://nonsense");

        var ex = await act.Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("mcpdb://nonsense");
    }

    [Fact]
    public async Task Unknown_prompt_is_reported_by_the_sdk()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var act = async () => await harness.Client.GetPromptAsync("no_such_prompt");

        var ex = await act.Should().ThrowAsync<McpException>();
        ex.Which.Message.Should().Contain("no_such_prompt");
    }
}
