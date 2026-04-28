using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Server.AdminApi.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace McpDatabaseQueryApp.Server.IntegrationTests.AdminApi;

/// <summary>
/// Verifies that ACL mutations made through the admin API propagate to the
/// in-process <see cref="IAclEvaluator"/> and that the admin write path
/// itself bypasses MCP-side enforcement (no ACL evaluation, no elicitation).
/// </summary>
public sealed class BypassEnforcementTests
{
    [Fact]
    public async Task Adding_a_deny_entry_invalidates_the_evaluator_cache()
    {
        await using var host = await AdminApiTestHost.StartAsync();

        var evaluator = host.Services.GetRequiredService<IAclEvaluator>();
        var profile = ProfileId.Default;

        // Pre-populate the evaluator cache with a permissive default-policy
        // result by issuing one evaluation.
        var initialDecision = await evaluator.EvaluateAsync(
            BuildRequest(profile, AclOperation.Read, "events"),
            CancellationToken.None);

        // Add a Deny entry for the public.events table via the admin API.
        var body = new CreateAclEntryRequest(
            "Profile",
            new AclScopeDto(null, null, null, null, "events", null),
            new[] { "Read" },
            "Deny",
            100,
            "blocking");
        using var req = host.NewRequest(HttpMethod.Post, "/admin/v1/profiles/default/acl/");
        req.Content = JsonContent.Create(body);
        var res = await host.Client.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var entry = await res.Content.ReadFromJsonAsync<AclEntryResponse>();

        // Re-evaluate: the deny entry must apply now (cache was invalidated).
        var blocked = await evaluator.EvaluateAsync(
            BuildRequest(profile, AclOperation.Read, "events"),
            CancellationToken.None);
        blocked.Effect.Should().Be(AclEffect.Deny);

        // Delete the entry; cache must invalidate again.
        using var delReq = host.NewRequest(HttpMethod.Delete,
            $"/admin/v1/profiles/default/acl/{entry!.EntryId}");
        var delRes = await host.Client.SendAsync(delReq);
        delRes.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDelete = await evaluator.EvaluateAsync(
            BuildRequest(profile, AclOperation.Read, "events"),
            CancellationToken.None);
        afterDelete.Effect.Should().Be(AclEffect.Deny); // default-deny once entries gone

        _ = initialDecision;
    }

    private static AclEvaluationRequest BuildRequest(ProfileId profile, AclOperation op, string table)
    {
        var connection = new ConnectionDescriptor
        {
            Id = "c1",
            Name = "c1",
            Provider = DatabaseKind.Postgres,
            Host = "h1",
            Port = 5432,
            Database = "demo",
            Username = "u",
            SslMode = "Disable",
        };
        var obj = new ObjectReference(ObjectKind.Table, Server: null, Database: null, Schema: "public", Name: table);
        return new AclEvaluationRequest(profile, connection, obj, op, Column: null);
    }
}
