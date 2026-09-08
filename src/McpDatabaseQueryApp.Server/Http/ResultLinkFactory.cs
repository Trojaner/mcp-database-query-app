using System.Text.Json;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Results;
using Microsoft.AspNetCore.Http;

namespace McpDatabaseQueryApp.Server.Http;

/// <summary>
/// A minted link handed back to a tool caller.
/// </summary>
/// <param name="Url">Absolute URL that serves the JSON payload.</param>
/// <param name="ExpiresAt">When the URL stops resolving.</param>
public sealed record ResultLinkDescriptor(string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// Turns a tool result into a short pre-authenticated HTTP URL.
/// </summary>
/// <remarks>
/// <para>
/// The payload stored behind the URL is the tool result serialized with the
/// very same <see cref="JsonSerializerOptions"/> the MCP transport would have
/// used, so <c>output="link"</c> and <c>output="inline"</c> deliver identical
/// JSON — one through the model's context, one over HTTP.
/// </para>
/// <para>
/// The token in the URL <em>is</em> the credential: 128 bits of entropy, no
/// other authentication, no profile check on read. That is deliberate (the
/// link is meant to be pasted into a browser or curl), which is why links are
/// short-lived, in-memory only, and must never be minted for anything the
/// caller could not already read.
/// </para>
/// </remarks>
public sealed class ResultLinkFactory
{
    private readonly IResultLinkStore _store;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;

    public ResultLinkFactory(
        IResultLinkStore store,
        McpDatabaseQueryAppOptions options,
        IHttpContextAccessor httpContextAccessor)
    {
        _store = store;
        _options = options;
        _httpContextAccessor = httpContextAccessor;
    }

    /// <summary>Whether <c>output="link"</c> is available on this server.</summary>
    public bool Enabled => _options.ResultLinks.Enabled;

    /// <summary>
    /// Serializes <paramref name="value"/>, stores it, and returns the URL
    /// that serves it back.
    /// </summary>
    /// <param name="value">The tool result to publish.</param>
    /// <param name="type">Declared type to serialize <paramref name="value"/> as.</param>
    /// <exception cref="InvalidOperationException">
    /// Result links are disabled, or no public base URL could be determined.
    /// </exception>
    public ResultLinkDescriptor Create(object value, Type type)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(type);

        if (!Enabled)
        {
            throw new InvalidOperationException(
                "output=\"link\" is disabled on this server. Set McpDatabaseQueryApp:ResultLinks:Enabled=true, or call the tool with output=\"inline\".");
        }

        var baseUrl = ResolveBaseUrl();
        var json = JsonSerializer.Serialize(value, type, McpDatabaseQueryAppJsonOptions.Tool);
        var link = _store.Create(json, "application/json; charset=utf-8");
        return new ResultLinkDescriptor($"{baseUrl}{ResultLinkEndpointRoutes.BasePath}/{link.Token}", link.ExpiresAt);
    }

    /// <summary>
    /// Works out the origin links are built against: the explicit
    /// <c>ResultLinks:BaseUrl</c> first, then the inbound request (which is
    /// correct for a directly exposed server), then the first configured HTTP
    /// listen URL as a development convenience.
    /// </summary>
    private string ResolveBaseUrl()
    {
        var configured = _options.ResultLinks.BaseUrl;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured.TrimEnd('/');
        }

        var request = _httpContextAccessor.HttpContext?.Request;
        if (request is not null && request.Host.HasValue)
        {
            return $"{request.Scheme}://{request.Host.Value}{request.PathBase.Value}".TrimEnd('/');
        }

        if (TryDeriveFromListenUrls(_options.Transport.Http, out var derived))
        {
            return derived;
        }

        throw new InvalidOperationException(
            "Cannot build a result link: this server has no reachable HTTP base URL. Set McpDatabaseQueryApp:ResultLinks:BaseUrl (e.g. https://db.example.internal), or call the tool with output=\"inline\".");
    }

    private static bool TryDeriveFromListenUrls(HttpTransportOptions http, out string baseUrl)
    {
        baseUrl = string.Empty;
        if (!http.Enabled || string.IsNullOrWhiteSpace(http.Urls))
        {
            return false;
        }

        foreach (var candidate in http.Urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
            {
                continue;
            }

            // Kestrel's wildcard bindings are not addressable as written.
            var host = uri.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+" ? "localhost" : uri.Host;
            baseUrl = new UriBuilder(uri.Scheme, host, uri.Port).Uri.GetLeftPart(UriPartial.Authority);
            return true;
        }

        return false;
    }
}
