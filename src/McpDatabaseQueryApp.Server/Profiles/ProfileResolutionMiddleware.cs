using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.Profiles;

/// <summary>
/// ASP.NET Core middleware that runs after authentication and opens an
/// <see cref="IProfileScope"/> for the resolved profile. All downstream
/// MCP handlers and tools see the per-request profile via
/// <see cref="IProfileContextAccessor"/>.
/// </summary>
/// <remarks>
/// <para>If JWT validation passed, the resolver chain maps the bearer
/// identity to a profile (auto-provisioning when configured). If no token
/// is present (or auth was disabled at startup), the chain falls back to
/// the default profile.</para>
/// <para>An <see cref="UnauthorizedAccessException"/> from the resolver
/// (e.g. unknown subject when auto-provisioning is disabled) is converted
/// into a 403 response.</para>
/// </remarks>
public sealed class ProfileResolutionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IProfileResolver _resolver;
    private readonly IProfileContextAccessor _accessor;
    private readonly ILogger<ProfileResolutionMiddleware> _logger;

    /// <summary>
    /// Creates a new <see cref="ProfileResolutionMiddleware"/>.
    /// </summary>
    public ProfileResolutionMiddleware(
        RequestDelegate next,
        IProfileResolver resolver,
        IProfileContextAccessor accessor,
        ILogger<ProfileResolutionMiddleware> logger)
    {
        _next = next;
        _resolver = resolver;
        _accessor = accessor;
        _logger = logger;
    }

    /// <summary>
    /// ASP.NET Core middleware invocation.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var auth = new HttpProfileAuthContext(context);
        Profile profile;
        try
        {
            profile = await _resolver.ResolveAsync(auth, context.RequestAborted).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Resolver returned null profile.");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Profile resolution refused inbound request");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync(ex.Message).ConfigureAwait(false);
            return;
        }

        using var scope = _accessor.Begin(profile);
        await _next(context).ConfigureAwait(false);
    }
}
