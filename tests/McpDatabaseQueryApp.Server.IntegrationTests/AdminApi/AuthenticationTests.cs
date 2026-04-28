using System.Net;
using FluentAssertions;
using McpDatabaseQueryApp.Server.AdminApi;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests.AdminApi;

public sealed class AuthenticationTests
{
    [Fact]
    public async Task Healthz_does_not_require_an_api_key()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        // Bypass the host's helper so we don't add the API key header.
        using var response = await host.Client.GetAsync("/admin/v1/healthz");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Missing_api_key_returns_401_with_challenge()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        using var response = await host.Client.GetAsync("/admin/v1/profiles/");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Should().ContainSingle()
            .Which.Scheme.Should().Be("ApiKey");
    }

    [Fact]
    public async Task Wrong_api_key_returns_401()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/admin/v1/profiles/");
        request.Headers.Add(AdminApiKeyAuthenticationHandler.HeaderName, "definitely-not-the-key");
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Right_api_key_is_accepted()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        using var request = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/");
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Disallowed_host_returns_403()
    {
        await using var host = await AdminApiTestHost.StartAsync(
            allowedHosts: new[] { "only-this-host.example" });

        using var request = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/");
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Http_request_is_rejected_when_https_is_required()
    {
        await using var host = await AdminApiTestHost.StartAsync(requireHttps: true);

        // The TestServer client BaseAddress is https; rebuild a plain http URI
        // so the IsHttps check fires.
        using var request = host.NewRequest(HttpMethod.Get, "http://localhost/admin/v1/profiles/");
        using var response = await host.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.MisdirectedRequest);
    }
}
