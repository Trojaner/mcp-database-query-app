using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using McpDatabaseQueryApp.Server.AdminApi.Models;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests.AdminApi;

public sealed class IsolationCrudTests
{
    [Fact]
    public async Task Create_equality_rule_then_list_filtered_by_connection()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var body = new CreateIsolationRuleRequest(
            Scope: new IsolationScopeDto(
                Host: "h1",
                Port: 5432,
                DatabaseName: "demo",
                Schema: "public",
                Table: "events"),
            Filter: new IsolationFilterDto(
                Kind: "Equality",
                Column: "tenant_id",
                Value: "alice",
                Values: null,
                Predicate: null,
                Parameters: null),
            Priority: 10,
            Description: "tenant filter");
        using var req = host.NewRequest(HttpMethod.Post,
            "/admin/v1/profiles/default/isolation-rules/");
        req.Content = JsonContent.Create(body);
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await res.Content.ReadFromJsonAsync<IsolationRuleResponse>();
        created!.Source.Should().Be("Dynamic");

        using var listReq = host.NewRequest(HttpMethod.Get,
            "/admin/v1/profiles/default/isolation-rules/?host=h1&port=5432&database=demo");
        var listRes = await host.Client.SendAsync(listReq);
        listRes.StatusCode.Should().Be(HttpStatusCode.OK);
        var list = await listRes.Content.ReadFromJsonAsync<List<IsolationRuleResponse>>();
        list!.Should().Contain(r => r.RuleId == created.RuleId);

        using var delReq = host.NewRequest(HttpMethod.Delete,
            $"/admin/v1/profiles/default/isolation-rules/{created.RuleId}");
        var delRes = await host.Client.SendAsync(delReq);
        delRes.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Create_in_list_rule()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var body = new CreateIsolationRuleRequest(
            Scope: new IsolationScopeDto("h1", 5432, "demo", "public", "events"),
            Filter: new IsolationFilterDto(
                Kind: "InList",
                Column: "tenant_id",
                Value: null,
                Values: new object?[] { "alice", "bob", "carol" },
                Predicate: null,
                Parameters: null),
            Priority: 10,
            Description: null);
        using var req = host.NewRequest(HttpMethod.Post,
            "/admin/v1/profiles/default/isolation-rules/");
        req.Content = JsonContent.Create(body);
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Static_rule_delete_returns_409()
    {
        var overrides = new Dictionary<string, string?>
        {
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Id"] = "static-rule-1",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:ProfileId"] = "default",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Host"] = "h1",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Port"] = "5432",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:DatabaseName"] = "demo",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Schema"] = "public",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Table"] = "events",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Filter:Kind"] = "Equality",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Filter:Column"] = "tenant",
            ["McpDatabaseQueryApp:DataIsolation:StaticRules:0:Filter:Value"] = "x",
        };

        await using var host = await AdminApiTestHost.StartAsync(overrides);

        using var req = host.NewRequest(HttpMethod.Delete,
            "/admin/v1/profiles/default/isolation-rules/static-rule-1");
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
}
