using McpDatabaseQueryApp.Server.AdminApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Lives in the Microsoft.Extensions.DependencyInjection namespace so callers
// can `services.AddAdminApi(configuration)` without an extra using.
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI helpers for the admin REST API.
/// </summary>
public static class AdminApiServiceCollectionExtensions
{
    /// <summary>
    /// Authorization policy name applied to every <c>/admin/v1/*</c> endpoint
    /// (except the public <c>/admin/v1/healthz</c> probe).
    /// </summary>
    public const string PolicyName = "AdminApi";

    /// <summary>
    /// Registers options, the API key provider, the authentication scheme,
    /// and the authorization policy used by the admin REST API. Idempotent.
    /// </summary>
    /// <remarks>
    /// Callers should still gate calls to
    /// <c>MapAdminApi()</c> on
    /// <c>AdminApiOptions.Enabled</c> — when the API is disabled, we do not
    /// want the scheme registered either, so this method short-circuits and
    /// only binds the options object.
    /// </remarks>
    public static IServiceCollection AddAdminApi(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AdminApiOptions>()
            .Configure(o =>
            {
                // Clear defaults before binding so configured collections fully
                // replace rather than append to the static defaults declared on
                // AdminApiOptions itself.
                o.AllowedHosts = new List<string>();
            })
            .Bind(configuration.GetSection(AdminApiOptions.SectionName));
        services.TryAddSingleton(sp =>
            sp.GetRequiredService<Options.IOptions<AdminApiOptions>>().Value);

        var snapshot = configuration.GetSection(AdminApiOptions.SectionName).Get<AdminApiOptions>()
            ?? new AdminApiOptions();
        if (!snapshot.Enabled)
        {
            return services;
        }

        services.TryAddSingleton<IAdminApiKeyProvider, AdminApiKeyProvider>();
        services.AddProblemDetails();
        services.AddOpenApi("admin-v1");

        services.AddAuthentication(AdminApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AdminApiKeyAuthenticationOptions, AdminApiKeyAuthenticationHandler>(
                AdminApiKeyAuthenticationHandler.SchemeName,
                _ => { });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolicyName, policy =>
            {
                policy.AddAuthenticationSchemes(AdminApiKeyAuthenticationHandler.SchemeName);
                policy.RequireAuthenticatedUser();
                policy.RequireClaim(AdminApiKeyAuthenticationHandler.AdminClaimType, "true");
            });

        return services;
    }
}
