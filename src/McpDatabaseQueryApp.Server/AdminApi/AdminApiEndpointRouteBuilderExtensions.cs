using McpDatabaseQueryApp.Server.AdminApi;
using McpDatabaseQueryApp.Server.AdminApi.Endpoints;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Lives in the Microsoft.AspNetCore.Builder namespace so callers can
// `app.MapAdminApi()` without an extra using.
// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Endpoint mapping helpers for the admin REST API.
/// </summary>
public static class AdminApiEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps the admin REST API onto <c>/admin/v1</c>. Honors
    /// <see cref="AdminApiOptions"/>: when <c>Enabled=false</c> this is a
    /// no-op. The host filter and HTTPS gate are enforced by route filters
    /// installed inside this method so the rest of the host pipeline is
    /// unaffected.
    /// </summary>
    public static IEndpointRouteBuilder MapAdminApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<AdminApiOptions>();
        if (!options.Enabled)
        {
            return endpoints;
        }

        var v1 = endpoints.MapGroup("/admin/v1")
            .AddEndpointFilter(new HttpsAndHostFilter(options));

        // Public health probe (no policy, no key, but still HTTPS / host gated).
        v1.MapHealthEndpoints();

        // Authenticated endpoint group: require the AdminApi policy on top of
        // the HTTPS / host filter inherited from /admin/v1.
        var secured = v1.MapGroup("/")
            .RequireAuthorization(AdminApiServiceCollectionExtensions.PolicyName);

        secured.MapProfileEndpoints();
        secured.MapAclEndpoints();
        secured.MapIsolationRuleEndpoints();

        // OpenAPI document, served at /admin/v1/openapi.json. Mapping it on
        // the secured group means operators must supply the API key to
        // retrieve the schema — consistent with the rest of the surface.
        secured.MapOpenApi("/openapi.json");

        return endpoints;
    }

    /// <summary>
    /// Endpoint filter that enforces the HTTPS requirement and the host
    /// allow-list. Runs before the endpoint handler; failures short-circuit
    /// with a problem-details body.
    /// </summary>
    private sealed class HttpsAndHostFilter : IEndpointFilter
    {
        private readonly AdminApiOptions _options;

        public HttpsAndHostFilter(AdminApiOptions options)
        {
            _options = options;
        }

        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(next);

            var http = context.HttpContext;
            if (_options.RequireHttps && !http.Request.IsHttps)
            {
                http.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return Results.Problem(
                    statusCode: StatusCodes.Status421MisdirectedRequest,
                    title: "HTTPS Required",
                    detail: "The admin API requires HTTPS. Disable McpDatabaseQueryApp:AdminApi:RequireHttps for local testing.",
                    type: "https://tools.ietf.org/html/rfc7807");
            }

            if (_options.AllowedHosts is { Count: > 0 } allowed)
            {
                var hostHeader = http.Request.Host.Host;
                var match = false;
                for (var i = 0; i < allowed.Count; i++)
                {
                    if (string.Equals(allowed[i], hostHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        match = true;
                        break;
                    }
                }
                if (!match)
                {
                    var logger = http.RequestServices
                        .GetRequiredService<ILoggerFactory>()
                        .CreateLogger("McpDatabaseQueryApp.Server.AdminApi");
                    logger.LogWarning(
                        "AdminApi rejected request from disallowed host '{Host}' on path {Path}.",
                        hostHeader,
                        http.Request.Path.Value);
                    http.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Results.Problem(
                        statusCode: StatusCodes.Status403Forbidden,
                        title: "Forbidden Host",
                        detail: $"Host '{hostHeader}' is not on the AdminApi allow-list.",
                        type: "https://tools.ietf.org/html/rfc7807");
                }
            }

            return await next(context).ConfigureAwait(false);
        }
    }
}
