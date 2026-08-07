using FluentAssertions;
using McpDatabaseQueryApp.Server.Tasks;
using ModelContextProtocol;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// Covers the MCP tasks extension (<c>io.modelcontextprotocol/tasks</c>), which MCP 2026-07-28
/// moved out of the core protocol.
/// </summary>
/// <remarks>
/// The taskable-tool list is deliberately config-driven, so these tests point it at
/// <c>scripts_list</c> — a tool that needs no live database — and exercise the real task
/// pipeline end to end: create, poll, and collect the result through a server-minted handle.
/// </remarks>
public sealed class McpTaskTests
{
    private static Dictionary<string, string?> TaskableScriptsList() => new()
    {
        ["McpDatabaseQueryApp:Tasks:Enabled"] = "true",
        ["McpDatabaseQueryApp:Tasks:TaskableTools:0"] = "scripts_list",
    };

    private static CallToolRequestParams ScriptsList() => new()
    {
        Name = "scripts_list",
        Arguments = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
        {
            ["cursor"] = System.Text.Json.JsonSerializer.SerializeToElement((string?)null),
            ["filter"] = System.Text.Json.JsonSerializer.SerializeToElement((string?)null),
        },
    };

    [Fact]
    public async Task A_taskable_tool_returns_a_task_handle_that_resolves_to_the_real_result()
    {
        await using var harness = await InProcessServerHarness.StartAsync(configOverrides: TaskableScriptsList());

        await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "task-visible",
                description = "d",
                sqlText = "SELECT 1;",
                destructive = false,
                tags = Array.Empty<string>(),
            },
        });

        var outcome = await harness.Client.CallToolAsTaskAsync(ScriptsList());

        outcome.IsTask.Should().BeTrue("scripts_list is configured as taskable");
        outcome.TaskCreated.Should().NotBeNull();
        outcome.TaskCreated!.TaskId.Should().NotBeNullOrWhiteSpace();
        outcome.TaskCreated.Status.Should().Be(McpTaskStatus.Working);

        var final = await PollToCompletionAsync(harness, outcome.TaskCreated!.TaskId);

        final.Should().BeOfType<CompletedTaskResult>();
        var completed = (CompletedTaskResult)final;
        completed.Status.Should().Be(McpTaskStatus.Completed);
        completed.Result.GetRawText().Should().Contain("task-visible");
    }

    [Fact]
    public async Task A_tool_that_is_not_taskable_still_returns_a_direct_result()
    {
        await using var harness = await InProcessServerHarness.StartAsync(configOverrides: TaskableScriptsList());

        // scripts_get is absent from TaskableTools, so asking for a task must not produce one.
        var outcome = await harness.Client.CallToolAsTaskAsync(new CallToolRequestParams
        {
            Name = "scripts_get",
            Arguments = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
            {
                ["nameOrId"] = System.Text.Json.JsonSerializer.SerializeToElement("missing"),
            },
        });

        outcome.IsTask.Should().BeFalse();
        outcome.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task An_unknown_task_handle_is_not_resolvable()
    {
        await using var harness = await InProcessServerHarness.StartAsync(configOverrides: TaskableScriptsList());

        var act = async () => await harness.Client.GetTaskAsync(Guid.NewGuid().ToString("N"));

        await act.Should().ThrowAsync<McpException>();
    }

    [Fact]
    public async Task Tasks_are_absent_when_the_extension_is_disabled()
    {
        await using var harness = await InProcessServerHarness.StartAsync(configOverrides: new Dictionary<string, string?>
        {
            ["McpDatabaseQueryApp:Tasks:Enabled"] = "false",
        });

        var outcome = await harness.Client.CallToolAsTaskAsync(ScriptsList());

        // With the extension unregistered the server just answers normally.
        outcome.IsTask.Should().BeFalse();
        outcome.Result.Should().NotBeNull();
    }

    private static async Task<GetTaskResult> PollToCompletionAsync(InProcessServerHarness harness, string taskId)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var status = await harness.Client.GetTaskAsync(taskId);
            if (status is not WorkingTaskResult)
            {
                return status;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Task {taskId} did not leave the working state.");
    }
}

/// <summary>Unit coverage for the taskable-tool policy.</summary>
public sealed class McpTaskExecutionPolicyTests
{
    private static McpTaskExecutionPolicy Policy(McpTaskOptions options) => new(options);

    [Fact]
    public void Listed_tools_are_optional_and_everything_else_is_synchronous()
    {
        var policy = Policy(new McpTaskOptions { TaskableTools = { "db_query" } });

        policy.Select("db_query").Should().Be(McpTaskExecutionMode.Optional);
        policy.Select("scripts_get").Should().Be(McpTaskExecutionMode.Synchronous);
    }

    [Fact]
    public void Required_tools_are_required()
    {
        var policy = Policy(new McpTaskOptions { RequiredTaskTools = { "db_query" } });

        policy.Select("db_query").Should().Be(McpTaskExecutionMode.Required);
    }

    [Fact]
    public void Disabling_the_extension_forces_everything_synchronous()
    {
        var policy = Policy(new McpTaskOptions { Enabled = false, TaskableTools = { "db_query" } });

        policy.Select("db_query").Should().Be(McpTaskExecutionMode.Synchronous);
    }
}
