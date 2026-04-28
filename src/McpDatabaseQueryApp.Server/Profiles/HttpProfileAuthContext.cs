using System.Security.Claims;
using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.AspNetCore.Http;

namespace McpDatabaseQueryApp.Server.Profiles;

/// <summary>
/// HTTP/SSE-transport <see cref="IProfileAuthContext"/>. Wraps the current
/// <see cref="HttpContext.User"/> and surfaces only the OAuth2 identifier
/// triple (<c>issuer</c>, <c>subject</c>, optional <c>preferred_username</c>)
/// that the resolver pipeline needs.
/// </summary>
public sealed class HttpProfileAuthContext : IProfileAuthContext
{
    private readonly HttpContext _httpContext;

    /// <summary>
    /// Creates a new <see cref="HttpProfileAuthContext"/>.
    /// </summary>
    public HttpProfileAuthContext(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        _httpContext = httpContext;
    }

    /// <inheritdoc/>
    public bool IsAuthenticated => _httpContext.User.Identity?.IsAuthenticated == true;

    /// <inheritdoc/>
    public string? Issuer
        => _httpContext.User.FindFirst("iss")?.Value
            ?? _httpContext.User.FindFirst(ClaimTypes.Authentication)?.Issuer;

    /// <inheritdoc/>
    public string? Subject
        => _httpContext.User.FindFirst("sub")?.Value
            ?? _httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    /// <inheritdoc/>
    public string? PreferredName
        => _httpContext.User.FindFirst("preferred_username")?.Value
            ?? _httpContext.User.FindFirst("name")?.Value
            ?? _httpContext.User.Identity?.Name;
}
