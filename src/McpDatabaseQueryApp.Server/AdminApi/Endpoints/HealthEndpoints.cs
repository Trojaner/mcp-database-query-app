using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace McpDatabaseQueryApp.Server.AdminApi.Endpoints;

/// <summary>
/// Public health probe for the admin API. Lives under <c>/admin/v1/healthz</c>
/// and is the only route that bypasses the API-key check, so an operator can
/// confirm the surface is reachable before configuring credentials.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>Maps the health endpoint onto the supplied route group.</summary>
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var version = typeof(HealthEndpoints).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(HealthEndpoints).Assembly.GetName().Version?.ToString()
            ?? "0.0.0";

        group.MapGet("/healthz", () => Results.Ok(new HealthResponse("ok", version)))
            .AllowAnonymous()
            .WithTags("Health")
            .WithSummary("Health probe (no auth required).");

        return group;
    }

    /// <summary>Body shape returned by <c>GET /admin/v1/healthz</c>.</summary>
    public sealed record HealthResponse(string Status, string Version);
}
