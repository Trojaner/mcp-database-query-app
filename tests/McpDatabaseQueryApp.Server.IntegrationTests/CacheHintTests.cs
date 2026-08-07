using FluentAssertions;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// Covers the <c>ttlMs</c> / <c>cacheScope</c> freshness hints required on list results
/// by MCP 2026-07-28 (SEP-2549), and the deterministic <c>tools/list</c> ordering.
/// </summary>
public sealed class CacheHintTests
{
    [Fact]
    public async Task Tools_list_carries_private_cache_hints()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.ListToolsAsync(new ListToolsRequestParams());

        result.TimeToLive.Should().NotBeNull();
        result.TimeToLive!.Value.Should().BePositive();
        // Tool visibility is profile- and ACL-dependent, so a shared intermediary must
        // never be allowed to serve one caller's listing to another.
        result.CacheScope.Should().Be(CacheScope.Private);
    }

    [Fact]
    public async Task Tools_list_is_ordered_deterministically()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var first = await harness.Client.ListToolsAsync(new ListToolsRequestParams());
        var second = await harness.Client.ListToolsAsync(new ListToolsRequestParams());

        var firstNames = first.Tools.Select(t => t.Name).ToList();
        firstNames.Should().BeInAscendingOrder(StringComparer.Ordinal);
        firstNames.Should().Equal(second.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task Resources_list_carries_private_cache_hints()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.ListResourcesAsync(new ListResourcesRequestParams());

        result.TimeToLive.Should().NotBeNull();
        result.CacheScope.Should().Be(CacheScope.Private);
    }

    [Fact]
    public async Task Resource_read_carries_private_cache_hints()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.ReadResourceAsync("mcpdb://databases");

        result.TimeToLive.Should().NotBeNull();
        result.CacheScope.Should().Be(CacheScope.Private);
    }
}
