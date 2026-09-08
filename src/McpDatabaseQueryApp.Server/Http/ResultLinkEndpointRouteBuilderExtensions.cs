using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Server.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

// Lives in the Microsoft.AspNetCore.Builder namespace so callers can
// `app.MapResultLinks()` without an extra using — same convention as MapAdminApi.
// ReSharper disable once CheckNamespace
namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Endpoint mapping for pre-authenticated query result links.
/// </summary>
public static class ResultLinkEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET /r/{token}</c>, which serves the JSON result a query tool
    /// parked when it was called with <c>output="link"</c>. No-op when
    /// <see cref="ResultLinkOptions.Enabled"/> is false.
    /// </summary>
    /// <remarks>
    /// The endpoint is deliberately anonymous: the unguessable token is the
    /// credential. Unknown, malformed and expired tokens all return 404 so the
    /// response cannot be used to probe which links exist.
    /// </remarks>
    public static IEndpointRouteBuilder MapResultLinks(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = endpoints.ServiceProvider.GetRequiredService<McpDatabaseQueryAppOptions>();
        if (!options.ResultLinks.Enabled)
        {
            return endpoints;
        }

        endpoints.MapGet($"{ResultLinkEndpointRoutes.BasePath}/{{token}}", static (
            string token,
            IResultLinkStore store,
            HttpResponse response) =>
        {
            var content = store.Resolve(token);
            if (content is null)
            {
                return Results.NotFound();
            }

            // Results are per-caller and short-lived: keep them out of shared
            // caches, and stop a browser sniffing the body as anything but JSON.
            response.Headers.CacheControl = "private, no-store";
            response.Headers.XContentTypeOptions = "nosniff";
            response.Headers.Expires = content.ExpiresAt.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return Results.Content(content.Body, content.ContentType);
        }).AllowAnonymous();

        return endpoints;
    }
}
