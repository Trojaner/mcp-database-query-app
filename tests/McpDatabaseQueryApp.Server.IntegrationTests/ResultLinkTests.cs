using System.Net;
using System.Text.Json;
using FluentAssertions;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Server.Http;
using McpDatabaseQueryApp.Server.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests;

/// <summary>
/// Covers the <c>output="link"</c> surface end to end: the tool argument, the
/// URL the factory mints, and the HTTP endpoint that serves the JSON back.
/// </summary>
public sealed class ResultLinkTests
{
    [Fact]
    public async Task Query_tools_advertise_the_output_argument()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var tools = await harness.Client.ListToolsAsync();

        foreach (var name in new[] { "db_query", "db_query_next_page", "db_explain" })
        {
            var tool = tools.Single(t => t.Name == name);
            var json = JsonSerializer.Serialize(tool.ProtocolTool.InputSchema);
            json.Should().Contain("output", because: $"{name} must expose the output mode argument");
            json.Should().Contain("link");
        }
    }

    [Fact]
    public async Task Unknown_output_mode_is_refused_before_the_query_runs()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.CallToolAsync("db_query", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["connectionId"] = "conn_does_not_exist",
                ["sql"] = "SELECT 1",
                ["output"] = "ftp",
            },
        });

        var text = ToolErrorReportingTests.ErrorText(result);
        // The mode is validated ahead of the connection lookup, so the error is
        // about the argument rather than the missing connection.
        text.Should().Contain("ftp").And.Contain("inline").And.Contain("link");
        text.Should().NotContain("conn_does_not_exist");
    }

    [Fact]
    public async Task Link_mode_is_rejected_alongside_a_csv_path()
    {
        await using var harness = await InProcessServerHarness.StartAsync();

        var result = await harness.Client.CallToolAsync("db_query", new Dictionary<string, object?>
        {
            ["args"] = new Dictionary<string, object?>
            {
                ["connectionId"] = "conn_does_not_exist",
                ["sql"] = "SELECT 1",
                ["output"] = "link",
                ["csvPath"] = "/tmp/out.csv",
            },
        });

        ToolErrorReportingTests.ErrorText(result).Should().Contain("mutually exclusive");
    }

    [Fact]
    public void Factory_prefers_the_configured_base_url_over_the_listen_urls()
    {
        var options = new McpDatabaseQueryAppOptions();
        options.ResultLinks.BaseUrl = "https://db.example.internal/mcp/";
        options.Transport.Http.Enabled = true;
        options.Transport.Http.Urls = "http://0.0.0.0:8080";

        var link = CreateFactory(options).Create(SampleResult(), typeof(QueryToolResult));

        link.Url.Should().StartWith("https://db.example.internal/mcp/r/");
    }

    [Fact]
    public void Factory_falls_back_to_the_listen_url_with_a_wildcard_host_rewritten()
    {
        var options = new McpDatabaseQueryAppOptions();
        options.Transport.Http.Enabled = true;
        options.Transport.Http.Urls = "http://0.0.0.0:8080;https://0.0.0.0:8443";

        var link = CreateFactory(options).Create(SampleResult(), typeof(QueryToolResult));

        // 0.0.0.0 is a bind address, not an address anyone can fetch.
        link.Url.Should().StartWith("http://localhost:8080/r/");
    }

    [Fact]
    public void Factory_refuses_to_mint_a_link_when_there_is_no_reachable_base_url()
    {
        var options = new McpDatabaseQueryAppOptions();
        options.Transport.Http.Enabled = false;

        var act = () => CreateFactory(options).Create(SampleResult(), typeof(QueryToolResult));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*ResultLinks:BaseUrl*");
    }

    [Fact]
    public void Factory_refuses_to_mint_a_link_when_the_feature_is_disabled()
    {
        var options = new McpDatabaseQueryAppOptions();
        options.ResultLinks.Enabled = false;
        options.ResultLinks.BaseUrl = "https://db.example.internal";

        var act = () => CreateFactory(options).Create(SampleResult(), typeof(QueryToolResult));

        act.Should().Throw<InvalidOperationException>().WithMessage("*disabled*");
    }

    [Fact]
    public async Task Endpoint_serves_the_json_payload_and_404s_everything_else()
    {
        var options = new McpDatabaseQueryAppOptions();
        var store = new InMemoryResultLinkStore(options, NullLogger<InMemoryResultLinkStore>.Instance);
        await using var server = await ResultLinkTestServer.StartAsync(options, store);

        var link = store.Create("{\"rowCount\":2}", "application/json; charset=utf-8");
        using var client = new HttpClient();

        var response = await client.GetAsync(new Uri($"{server.BaseAddress}r/{link.Token}"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Be("{\"rowCount\":2}");
        response.Headers.CacheControl!.ToString().Should().Contain("no-store");

        var tampered = await client.GetAsync(new Uri($"{server.BaseAddress}r/{link.Token[..^1]}A"));
        tampered.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var unknown = await client.GetAsync(new Uri($"{server.BaseAddress}r/not-a-real-token"));
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Endpoint_is_not_mapped_when_result_links_are_disabled()
    {
        var options = new McpDatabaseQueryAppOptions();
        options.ResultLinks.Enabled = false;
        var store = new InMemoryResultLinkStore(options, NullLogger<InMemoryResultLinkStore>.Instance);
        await using var server = await ResultLinkTestServer.StartAsync(options, store);

        var link = store.Create("{}", "application/json");
        using var client = new HttpClient();

        var response = await client.GetAsync(new Uri($"{server.BaseAddress}r/{link.Token}"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private static ResultLinkFactory CreateFactory(McpDatabaseQueryAppOptions options) =>
        new(
            new InMemoryResultLinkStore(options, NullLogger<InMemoryResultLinkStore>.Instance),
            options,
            new HttpContextAccessor());

    private static QueryToolResult SampleResult() => new(
        "conn_abc123",
        [],
        [],
        RowCount: 0,
        Truncated: false,
        ExecutionMs: 1,
        ResultSetId: null,
        TextTable: "(no rows, 1 ms)",
        CsvPath: null,
        ResultUrl: null,
        ResultUrlExpiresAt: null);

    /// <summary>
    /// A Kestrel host with nothing mapped but <c>MapResultLinks</c>, bound to an
    /// ephemeral port, so the endpoint is exercised over real HTTP.
    /// </summary>
    private sealed class ResultLinkTestServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ResultLinkTestServer(WebApplication app, string baseAddress)
        {
            _app = app;
            BaseAddress = baseAddress;
        }

        public string BaseAddress { get; }

        public static async Task<ResultLinkTestServer> StartAsync(
            McpDatabaseQueryAppOptions options,
            IResultLinkStore store)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(store);

            var app = builder.Build();
            app.MapResultLinks();
            await app.StartAsync();

            var address = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.First();
            return new ResultLinkTestServer(app, address.EndsWith('/') ? address : address + "/");
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
