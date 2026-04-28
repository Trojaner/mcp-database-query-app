using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using McpDatabaseQueryApp.Server.AdminApi.Models;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests.AdminApi;

public sealed class AclCrudTests
{
    [Fact]
    public async Task Create_then_list_then_patch_then_delete_a_dynamic_entry()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var create = new CreateAclEntryRequest(
            SubjectKind: "Profile",
            Scope: new AclScopeDto(
                Host: "localhost", Port: 5432, DatabaseName: "demo",
                Schema: "public", Table: "events", Column: null),
            AllowedOperations: new[] { "Read" },
            Effect: "Allow",
            Priority: 10,
            Description: "demo");
        using var createReq = host.NewRequest(HttpMethod.Post, "/admin/v1/profiles/default/acl/");
        createReq.Content = JsonContent.Create(create);
        var createRes = await host.Client.SendAsync(createReq);
        createRes.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createRes.Content.ReadFromJsonAsync<AclEntryResponse>();
        created!.Source.Should().Be("dynamic");
        created.ReadOnly.Should().BeFalse();

        using var listReq = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/default/acl/");
        var listRes = await host.Client.SendAsync(listReq);
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listRes.Content.ReadFromJsonAsync<List<AclEntryResponse>>();
        list!.Should().Contain(e => e.EntryId == created.EntryId);

        var patch = new PatchAclEntryRequest(
            Scope: null,
            AllowedOperations: new[] { "Read", "Insert" },
            Effect: "Deny",
            Priority: 99,
            Description: "updated");
        using var patchReq = host.NewRequest(HttpMethod.Patch,
            $"/admin/v1/profiles/default/acl/{created.EntryId}");
        patchReq.Content = JsonContent.Create(patch);
        var patchRes = await host.Client.SendAsync(patchReq);
        patchRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var patched = await patchRes.Content.ReadFromJsonAsync<AclEntryResponse>();
        patched!.Effect.Should().Be("Deny");
        patched.Priority.Should().Be(99);

        using var delReq = host.NewRequest(HttpMethod.Delete,
            $"/admin/v1/profiles/default/acl/{created.EntryId}");
        var delRes = await host.Client.SendAsync(delReq);
        delRes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Static_acl_entries_surface_as_read_only()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["McpDatabaseQueryApp:Authorization:StaticEntries:0:ProfileId"] = "default",
            ["McpDatabaseQueryApp:Authorization:StaticEntries:0:Effect"] = "Deny",
            ["McpDatabaseQueryApp:Authorization:StaticEntries:0:Priority"] = "5",
            ["McpDatabaseQueryApp:Authorization:StaticEntries:0:AllowedOperations:0"] = "Read",
            ["McpDatabaseQueryApp:Authorization:StaticEntries:0:Description"] = "static-block",
        };

        await using var host = await AdminApiTestHost.StartAsync(overrides);

        using var listReq = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/default/acl/");
        var listRes = await host.Client.SendAsync(listReq);
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listRes.Content.ReadFromJsonAsync<List<AclEntryResponse>>();
        list!.Should().Contain(e => e.Source == "static" && e.ReadOnly);
        var staticEntry = list!.First(e => e.Source == "static");

        // Deleting a static entry returns 409.
        using var delReq = host.NewRequest(HttpMethod.Delete,
            $"/admin/v1/profiles/default/acl/{staticEntry.EntryId}");
        var delRes = await host.Client.SendAsync(delReq);
        delRes.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Bulk_replace_succeeds()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var body = new ReplaceAclEntriesRequest(new[]
        {
            new CreateAclEntryRequest(
                "Profile",
                new AclScopeDto(null, null, null, null, "table-a", null),
                new[] { "Read" }, "Allow", 1, null),
            new CreateAclEntryRequest(
                "Profile",
                new AclScopeDto(null, null, null, null, "table-b", null),
                new[] { "Insert" }, "Deny", 2, null),
        });

        using var req = host.NewRequest(HttpMethod.Put, "/admin/v1/profiles/default/acl/");
        req.Content = JsonContent.Create(body);
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var entries = await res.Content.ReadFromJsonAsync<List<AclEntryResponse>>();
        entries!.Should().HaveCount(2);

        // Subsequent list should reflect only those two.
        using var listReq = host.NewRequest(HttpMethod.Get, "/admin/v1/profiles/default/acl/");
        var listRes = await host.Client.SendAsync(listReq);
        var list = await listRes.Content.ReadFromJsonAsync<List<AclEntryResponse>>();
        list!.Where(e => e.Source == "dynamic").Should().HaveCount(2);
    }
}
