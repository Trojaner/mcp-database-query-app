using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// Covers the confirmation that gates the grant of write access itself —
/// opening a connection that is not read-only, registering a pre-defined entry
/// that is not read-only, and flipping read-only off on an existing entry.
/// </summary>
/// <remarks>
/// Confirming individual statements is not enough on its own: a write-enabled
/// connection removes the hard block in front of every write path, and a
/// write-enabled pre-defined entry hands that out to every later session. These
/// tests assert the user is asked before that happens, that a refusal leaves the
/// stored state untouched, and that a read-only grant is never gated.
/// </remarks>
public sealed class WriteAccessConfirmationTests
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
                    Content = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["confirm"] = JsonSerializer.SerializeToElement(confirm),
                    },
                });
            },
        };

        return await InProcessServerHarness.StartAsync(clientHandlers: handlers);
    }

    private static Dictionary<string, object?> PredefinedArgs(string name, bool readOnly, bool confirm = false) =>
        new()
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["provider"] = "Postgres",
                ["host"] = "db.example.com",
                ["port"] = 5432,
                ["database"] = "analytics",
                ["username"] = "writer",
                ["password"] = "topsecret",
                ["readOnly"] = readOnly,
                ["confirm"] = confirm,
            },
        };

    private static Dictionary<string, object?> UpdateArgs(string name, bool readOnly, bool confirm = false) =>
        new()
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["provider"] = "Postgres",
                ["host"] = "db.example.com",
                ["port"] = 5432,
                ["database"] = "analytics",
                ["username"] = "writer",
                ["readOnly"] = readOnly,
                ["confirm"] = confirm,
            },
        };

    [Fact]
    public async Task Creating_a_write_enabled_predefined_database_asks_first()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: true, asked.Add);

        var created = await harness.Client.CallToolAsync("db_predefined_create", PredefinedArgs("writable", readOnly: false));

        created.IsError.Should().NotBe(true);
        asked.Should().ContainSingle();
        asked[0].Message.Should().Contain("writable").And.Contain("WRITE-ENABLED");
        UnwrappedText(created).Should().Contain("\"readOnly\":false");
    }

    [Fact]
    public async Task Declining_leaves_the_write_enabled_entry_unregistered()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: false, asked.Add);

        var created = await harness.Client.CallToolAsync("db_predefined_create", PredefinedArgs("never-stored", readOnly: false));

        ToolErrorReportingTests.ErrorText(created).Should().Contain("declined");
        asked.Should().ContainSingle();

        var get = await harness.Client.CallToolAsync(
            "db_predefined_get",
            new Dictionary<string, object?> { ["nameOrId"] = "never-stored" });
        ToolErrorReportingTests.ErrorText(get).Should().Contain("Not found");
    }

    [Fact]
    public async Task Creating_a_read_only_predefined_database_is_never_gated()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: false, asked.Add);

        var created = await harness.Client.CallToolAsync("db_predefined_create", PredefinedArgs("readonly-db", readOnly: true));

        created.IsError.Should().NotBe(true);
        asked.Should().BeEmpty("a read-only entry grants nothing that needs approving");
    }

    [Fact]
    public async Task Turning_read_only_off_asks_and_a_refusal_keeps_the_entry_read_only()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: false, asked.Add);

        var created = await harness.Client.CallToolAsync("db_predefined_create", PredefinedArgs("flip-me", readOnly: true));
        created.IsError.Should().NotBe(true);

        var updated = await harness.Client.CallToolAsync("db_predefined_update", UpdateArgs("flip-me", readOnly: false));

        ToolErrorReportingTests.ErrorText(updated).Should().Contain("declined");
        asked.Should().ContainSingle();
        asked[0].Message.Should().Contain("flip-me").And.Contain("read-only OFF");

        var get = await harness.Client.CallToolAsync(
            "db_predefined_get",
            new Dictionary<string, object?> { ["nameOrId"] = "flip-me" });
        UnwrappedText(get).Should().Contain("\"readOnly\":true");
    }

    [Fact]
    public async Task Turning_read_only_off_takes_effect_once_approved()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: true, asked.Add);

        await harness.Client.CallToolAsync("db_predefined_create", PredefinedArgs("approve-me", readOnly: true));

        var updated = await harness.Client.CallToolAsync("db_predefined_update", UpdateArgs("approve-me", readOnly: false));

        updated.IsError.Should().NotBe(true);
        asked.Should().ContainSingle();
        UnwrappedText(updated).Should().Contain("\"readOnly\":false");
    }

    [Fact]
    public async Task Updating_an_already_write_enabled_entry_does_not_re_ask()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: false, asked.Add);

        // confirm=true is honoured because the harness runs with
        // --dangerously-skip-permissions; the grant is approved once, here.
        var created = await harness.Client.CallToolAsync(
            "db_predefined_create",
            PredefinedArgs("already-writable", readOnly: false, confirm: true));
        created.IsError.Should().NotBe(true);
        asked.Should().BeEmpty();

        var updated = await harness.Client.CallToolAsync("db_predefined_update", UpdateArgs("already-writable", readOnly: false));

        updated.IsError.Should().NotBe(true);
        asked.Should().BeEmpty("the entry was already write-enabled, so nothing new is being granted");
    }

    [Fact]
    public async Task Opening_a_write_enabled_ad_hoc_connection_asks_before_connecting()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: false, asked.Add);

        var connect = await harness.Client.CallToolAsync("db_connect", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["provider"] = "Postgres",
                ["host"] = "db.invalid",
                ["port"] = 5432,
                ["database"] = "analytics",
                ["username"] = "writer",
                ["password"] = "topsecret",
                ["readOnly"] = false,
            },
        });

        // The refusal, not a connection failure, is what comes back — proof the
        // question is asked before the driver is ever dialled.
        ToolErrorReportingTests.ErrorText(connect).Should().Contain("declined");
        asked.Should().ContainSingle();
        asked[0].Message.Should().Contain("WRITE-ENABLED").And.Contain("db.invalid");
    }

    [Fact]
    public async Task Connecting_to_a_write_enabled_predefined_database_asks_too()
    {
        var asked = new List<ElicitRequestParams>();
        await using var harness = await StartWithAnswerAsync(confirm: false, asked.Add);

        await harness.Client.CallToolAsync(
            "db_predefined_create",
            PredefinedArgs("writable-target", readOnly: false, confirm: true));
        asked.Should().BeEmpty();

        var connect = await harness.Client.CallToolAsync("db_connect", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?> { ["name"] = "writable-target" },
        });

        ToolErrorReportingTests.ErrorText(connect).Should().Contain("declined");
        asked.Should().ContainSingle();
        asked[0].Message.Should().Contain("writable-target");
    }
}
