using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace McpDatabaseQueryApp.Server.DependencyInjection;

/// <summary>
/// DI helpers that wire the profile resolution pipeline into the host. The
/// stdio transport calls <see cref="AddProfiles"/> only; the HTTP/SSE
/// transport adds <see cref="AddOAuth2ProfileAuth"/> on top to opt into JWT
/// bearer validation.
/// </summary>
public static class ProfileServiceCollectionExtensions
{
    /// <summary>
    /// Registers the profile resolver pipeline. Call once for every transport.
    /// </summary>
    public static IServiceCollection AddProfiles(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<OAuth2ProfileResolver>();
        services.AddSingleton<IProfileResolver>(sp =>
        {
            var oauth = sp.GetRequiredService<OAuth2ProfileResolver>();
            var dflt = sp.GetRequiredService<DefaultProfileResolver>();
            return new CompositeProfileResolver(new IProfileResolver[] { oauth, dflt });
        });
        return services;
    }

    /// <summary>
    /// Registers JWT Bearer authentication using the configured
    /// <c>McpDatabaseQueryApp:OAuth2</c> section. If the section is empty or has no
    /// <see cref="OAuth2Options.Authority"/>, this is a no-op so the HTTP
    /// transport remains usable without an OIDC provider (every request resolves
    /// to the default profile).
    /// </summary>
    public static IServiceCollection AddOAuth2ProfileAuth(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var oauth = configuration
            .GetSection(McpDatabaseQueryAppOptions.SectionName)
            .GetSection("OAuth2")
            .Get<OAuth2Options>() ?? new OAuth2Options();

        services.AddSingleton(new ProfileResolutionOptions
        {
            AutoProvisionProfiles = oauth.AutoProvisionProfiles,
        });

        if (string.IsNullOrWhiteSpace(oauth.Authority))
        {
            return services;
        }

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = oauth.Authority;
                options.RequireHttpsMetadata = oauth.RequireHttps;
                if (!string.IsNullOrWhiteSpace(oauth.MetadataAddress))
                {
                    options.MetadataAddress = oauth.MetadataAddress;
                }

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = oauth.Authority,
                    ValidateAudience = !string.IsNullOrWhiteSpace(oauth.Audience),
                    ValidAudience = oauth.Audience,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                };

                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = ctx =>
                    {
                        var logger = ctx.HttpContext.RequestServices
                            .GetRequiredService<ILoggerFactory>()
                            .CreateLogger("McpDatabaseQueryApp.Server.OAuth2");
                        // Never log the token itself; just the failure reason.
                        logger.LogDebug("JWT authentication failed: {Reason}", ctx.Exception.GetType().Name);
                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        return services;
    }

    /// <summary>
    /// Hooks the <see cref="Profiles.ProfileResolutionMiddleware"/> into the
    /// request pipeline. Must be called after authentication so the middleware
    /// can read <see cref="Microsoft.AspNetCore.Http.HttpContext.User"/>.
    /// </summary>
    public static IApplicationBuilder UseProfileResolution(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<Profiles.ProfileResolutionMiddleware>();
    }

    // Suppress IDE0051 false positive on Options used only for binding.
    private static void _Touch(IOptions<OAuth2Options> _) { }
}
