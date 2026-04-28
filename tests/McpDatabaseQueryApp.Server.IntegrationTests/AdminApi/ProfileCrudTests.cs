using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using McpDatabaseQueryApp.Server.AdminApi.Models;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests.AdminApi;

public sealed class ProfileCrudTests
{
    [Fact]
    public async Task Default_profile_is_visible_in_list()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        using var request = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/");
        var response = await host.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await response.Content.ReadFromJsonAsync<PageResponse<ProfileResponse>>();
        page!.Items.Should().Contain(p => p.ProfileId == "default");
    }

    [Fact]
    public async Task Create_then_get_then_patch_then_delete()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var create = new CreateProfileRequest(
            ProfileId: "alice",
            Name: "Alice",
            Subject: "alice@example.com",
            Issuer: null,
            Status: null,
            Metadata: null);
        using var createReq = host.NewRequest(HttpMethod.Post, "/admin/v1/profiles/");
        createReq.Content = JsonContent.Create(create);
        var createRes = await host.Client.SendAsync(createReq);
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);

        using var getReq = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/alice");
        var getRes = await host.Client.SendAsync(getReq);
        getRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var got = await getRes.Content.ReadFromJsonAsync<ProfileResponse>();
        got!.Name.Should().Be("Alice");

        var patch = new PatchProfileRequest(Name: "Alice Smith", Status: "Disabled", Metadata: null);
        using var patchReq = host.NewRequest(HttpMethod.Patch, "/admin/v1/profiles/alice");
        patchReq.Content = JsonContent.Create(patch);
        var patchRes = await host.Client.SendAsync(patchReq);
        patchRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var patched = await patchRes.Content.ReadFromJsonAsync<ProfileResponse>();
        patched!.Name.Should().Be("Alice Smith");
        patched.Status.Should().Be("Disabled");

        using var delReq = host.NewRequest(HttpMethod.Delete, "/admin/v1/profiles/alice");
        var delRes = await host.Client.SendAsync(delReq);
        delRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var get2Req = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/alice");
        var get2Res = await host.Client.SendAsync(get2Req);
        get2Res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Default_profile_create_is_rejected_with_409()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var create = new CreateProfileRequest(
            ProfileId: "default",
            Name: "X",
            Subject: "default",
            Issuer: null,
            Status: null,
            Metadata: null);
        using var req = host.NewRequest(HttpMethod.Post, "/admin/v1/profiles/");
        req.Content = JsonContent.Create(create);
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Default_profile_delete_is_rejected_with_409()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        using var req = host.NewRequest(HttpMethod.Delete, "/admin/v1/profiles/default");
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Missing_profile_returns_404()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        using var req = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/does-not-exist");
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_without_name_returns_400()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var create = new CreateProfileRequest(
            ProfileId: "bob",
            Name: "",
            Subject: "bob",
            Issuer: null,
            Status: null,
            Metadata: null);
        using var req = host.NewRequest(HttpMethod.Post, "/admin/v1/profiles/");
        req.Content = JsonContent.Create(create);
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
