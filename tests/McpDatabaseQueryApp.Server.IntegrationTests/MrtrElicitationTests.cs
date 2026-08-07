using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// Covers the multi round-trip request (MRTR) elicitation path introduced by
/// MCP 2026-07-28 (SEP-2322).
/// </summary>
/// <remarks>
/// The server no longer sends <c>elicitation/create</c> to the client. It throws
/// <see cref="InputRequiredException"/>, the SDK turns that into an
/// <c>input_required</c> result, the client answers via its elicitation handler and
/// <b>re-issues the same tool call</b> with the answer in <c>InputResponses</c>.
/// These tests assert the answer actually survives that round trip — an accept must
/// change the outcome, which a gateway that silently returned <c>false</c> could not fake.
/// </remarks>
public sealed class MrtrElicitationTests
{
    private static string UnwrappedText(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;

    private static async Task<InProcessServerHarness> StartWithAnswerAsync(
        bool confirm,
        Action<ElicitRequestParams> onAsked)
    {
        var handlers = new McpClientHandlers
        {
            ElicitationHandler = (request, ct) =>
            {
                onAsked(request!);
                return ValueTask.FromResult(new ElicitResult
                {
                    Action = "accept",
                    Content = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal)
                    {
                        ["confirm"] = System.Text.Json.JsonSerializer.SerializeToElement(confirm),
                    },
                });
            },
        };

        return await InProcessServerHarness.StartAsync(clientHandlers: handlers);
    }

    private static async Task CreateScriptAsync(InProcessServerHarness harness, string name)
    {
        var create = await harness.Client.CallToolAsync("scripts_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name,
                description = "temp",
                sqlText = "SELECT 1;",
                destructive = false,
                tags = Array.Empty<string>(),
            },
        });
        create.IsError.Should().NotBe(true);
    }

    [Fact]
    public async Task Confirmed_elicitation_answer_survives_the_round_trip_and_deletes()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: true, asked.Add);
        await CreateScriptAsync(harness, "mrtr-accept");

        var deleted = await harness.Client.CallToolAsync("scripts_delete", new Dictionary<string, object?>
        {
            ["nameOrId"] = "mrtr-accept",
            ["confirm"] = false,
        });

        deleted.IsError.Should().NotBe(true);
        // The delete only happens if the client's "confirm: true" came back through
        // InputResponses on the retried call.
        UnwrappedText(deleted).Should().Contain("\"deleted\":true");

        asked.Should().ContainSingle("the tool must ask exactly once per logical invocation");
        asked[0].Message.Should().Contain("mrtr-accept");

        var get = await harness.Client.CallToolAsync("scripts_get", new Dictionary<string, object?>
        {
            ["nameOrId"] = "mrtr-accept",
        });
        UnwrappedText(get).Should().NotContain("\"name\":\"mrtr-accept\"");
    }

    [Fact]
    public async Task Rejected_elicitation_answer_leaves_the_script_in_place()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: false, asked.Add);
        await CreateScriptAsync(harness, "mrtr-reject");

        var deleted = await harness.Client.CallToolAsync("scripts_delete", new Dictionary<string, object?>
        {
            ["nameOrId"] = "mrtr-reject",
            ["confirm"] = false,
        });

        deleted.IsError.Should().NotBe(true);
        UnwrappedText(deleted).Should().Contain("\"deleted\":false");
        asked.Should().ContainSingle();

        var get = await harness.Client.CallToolAsync("scripts_get", new Dictionary<string, object?>
        {
            ["nameOrId"] = "mrtr-reject",
        });
        UnwrappedText(get).Should().Contain("mrtr-reject");
    }

    [Fact]
    public async Task Confirm_true_skips_the_question_entirely()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: true, asked.Add);
        await CreateScriptAsync(harness, "mrtr-preconfirmed");

        var deleted = await harness.Client.CallToolAsync("scripts_delete", new Dictionary<string, object?>
        {
            ["nameOrId"] = "mrtr-preconfirmed",
            ["confirm"] = true,
        });

        deleted.IsError.Should().NotBe(true);
        UnwrappedText(deleted).Should().Contain("\"deleted\":true");
        asked.Should().BeEmpty("an explicit confirm argument must not trigger a prompt");
    }
}
