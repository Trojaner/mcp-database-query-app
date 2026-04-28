using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDatabaseQueryApp.Server.AdminApi;

/// <summary>
/// Static parameters for <see cref="AdminApiKeyAuthenticationHandler"/>.
/// Inherits the base scheme options unchanged — the handler does not need any
/// per-scheme switches today.
/// </summary>
public sealed class AdminApiKeyAuthenticationOptions : AuthenticationSchemeOptions
{
}

/// <summary>
/// Authentication handler that reads the <c>X-Admin-Api-Key</c> request header
/// and constant-time-compares the bytes against the configured admin key. On
/// success a synthetic <see cref="ClaimsPrincipal"/> with the <c>admin</c>
/// claim set to <c>true</c> is attached to the request.
/// </summary>
/// <remarks>
/// <list type="bullet">
///   <item><description>Missing or empty header: no result. The endpoint
///   policy fails over to challenge → 401.</description></item>
///   <item><description>Wrong key: <see cref="AuthenticateResult.Fail(string)"/>
///   → challenge → 401.</description></item>
///   <item><description>The handler never logs the key value. Failures log
///   only that a comparison failed, plus the request path.</description></item>
/// </list>
/// </remarks>
public sealed class AdminApiKeyAuthenticationHandler : AuthenticationHandler<AdminApiKeyAuthenticationOptions>
{
    /// <summary>Authentication scheme name.</summary>
    public const string SchemeName = "AdminApiKey";

    /// <summary>Header name used to carry the API key.</summary>
    public const string HeaderName = "X-Admin-Api-Key";

    /// <summary>Claim type marking the synthetic admin principal.</summary>
    public const string AdminClaimType = "admin";

    private readonly IAdminApiKeyProvider _provider;

    /// <summary>Creates a new <see cref="AdminApiKeyAuthenticationHandler"/>.</summary>
    public AdminApiKeyAuthenticationHandler(
        IOptionsMonitor<AdminApiKeyAuthenticationOptions> options,
        ILoggerFactory loggerFactory,
        UrlEncoder encoder,
        IAdminApiKeyProvider provider)
        : base(options, loggerFactory, encoder)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var supplied = values[0];
        if (string.IsNullOrEmpty(supplied))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        if (!_provider.TryGetKeyBytes(out var expected))
        {
            Logger.LogError(
                "Admin API key is not configured but request {Path} carried an {Header} header.",
                Request.Path.Value,
                HeaderName);
            return Task.FromResult(AuthenticateResult.Fail("Admin API is not configured."));
        }

        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);

        // Constant-time comparison MUST run on equal-length buffers; pad the
        // shorter side with zero bytes so the comparison still touches every
        // byte and does not short-circuit on length mismatch. The explicit
        // length check below forces a mismatch when the two differed.
        var maxLen = Math.Max(suppliedBytes.Length, expected.Length);
        var a = new byte[maxLen];
        var b = new byte[maxLen];
        suppliedBytes.AsSpan().CopyTo(a);
        expected.CopyTo(b);

        var bytewiseEqual = CryptographicOperations.FixedTimeEquals(a, b);
        var lengthsEqual = suppliedBytes.Length == expected.Length;

        // Wipe the buffers we own; the provider's bytes are reused.
        CryptographicOperations.ZeroMemory(suppliedBytes);
        CryptographicOperations.ZeroMemory(a);
        CryptographicOperations.ZeroMemory(b);

        if (!bytewiseEqual || !lengthsEqual)
        {
            Logger.LogWarning(
                "Admin API key comparison failed for {Path}.",
                Request.Path.Value);
            return Task.FromResult(AuthenticateResult.Fail("Invalid admin API key."));
        }

        var identity = new ClaimsIdentity(SchemeName);
        identity.AddClaim(new Claim(AdminClaimType, "true"));
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "ApiKey";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }
}
