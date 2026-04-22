using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

public sealed class NoPasswordLeakTests
{
    private const string SentinelPassword = "k9ZGuardedPasswordHunter2024";

    [Fact]
    public async Task No_payload_ever_contains_the_sentinel_password()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        await harness.Client.CallToolAsync("db_predefined_create", new Dictionary<string, object?>
        {
            ["args"] = new
            {
                name = "canary",
                provider = "SqlServer",
                host = "example.com",
                port = 1433,
                database = "audit",
                username = "admin",
                password = SentinelPassword,
                sslMode = "Mandatory",
                trustServerCertificate = true,
            },
        });

        var payloads = new List<string>();

        payloads.Add(JsonSerializer.Serialize(await harness.Client.ListToolsAsync()));
        payloads.Add(JsonSerializer.Serialize(await harness.Client.ListResourcesAsync()));
        payloads.Add(JsonSerializer.Serialize(await harness.Client.ListPromptsAsync()));

        payloads.Add(JsonSerializer.Serialize(await harness.Client.CallToolAsync("db_list_predefined", new Dictionary<string, object?>())));
        payloads.Add(JsonSerializer.Serialize(await harness.Client.CallToolAsync("db_predefined_get", new Dictionary<string, object?> { ["nameOrId"] = "canary" })));
        payloads.Add(JsonSerializer.Serialize(await harness.Client.CallToolAsync("db_list_connections", new Dictionary<string, object?>())));

        payloads.Add(((TextResourceContents)(await harness.Client.ReadResourceAsync("mcpdb://databases")).Contents[0]).Text ?? string.Empty);
        payloads.Add(((TextResourceContents)(await harness.Client.ReadResourceAsync("mcpdb://databases/canary")).Contents[0]).Text ?? string.Empty);
        payloads.Add(((TextResourceContents)(await harness.Client.ReadResourceAsync("mcpdb://connections")).Contents[0]).Text ?? string.Empty);
        payloads.Add(((TextResourceContents)(await harness.Client.ReadResourceAsync("mcpdb://providers")).Contents[0]).Text ?? string.Empty);

        foreach (var payload in payloads)
        {
            payload.Should().NotContain(SentinelPassword, because: "passwords must NEVER leave the server in any MCP response");
        }
    }
}
