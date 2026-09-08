namespace McpDatabaseQueryApp.Core.Results;

/// <summary>
/// A minted result link. <see cref="Token"/> is the bearer credential that
/// appears in the URL — treat it as a secret; only <see cref="Id"/> is safe to
/// log.
/// </summary>
/// <param name="Id">Opaque lookup id (the non-secret prefix of the token).</param>
/// <param name="Token">Full URL token: lookup id + secret.</param>
/// <param name="CreatedAt">When the entry was stored.</param>
/// <param name="ExpiresAt">When the entry stops resolving.</param>
public sealed record ResultLink(string Id, string Token, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// A stored payload resolved from a result link.
/// </summary>
public sealed record ResultLinkContent(string Body, string ContentType, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Stores tool results that were requested as an HTTP resource instead of an
/// inline payload, keyed by a pre-authenticated token.
/// </summary>
/// <remarks>
/// Implementations are process-local and deliberately non-durable: entries are
/// dropped when the server restarts, which is the documented contract for the
/// <c>output="link"</c> tool mode.
/// </remarks>
public interface IResultLinkStore
{
    /// <summary>
    /// Stores <paramref name="body"/> and mints a token for it.
    /// </summary>
    /// <param name="body">Serialized payload to serve verbatim.</param>
    /// <param name="contentType">Response content type.</param>
    /// <param name="ttl">Lifetime override; defaults to the configured TTL.</param>
    ResultLink Create(string body, string contentType, TimeSpan? ttl = null);

    /// <summary>
    /// Resolves a token to its payload, or <see langword="null"/> when the
    /// token is unknown, malformed or expired. All three cases are
    /// indistinguishable to the caller by design.
    /// </summary>
    ResultLinkContent? Resolve(string token);

    /// <summary>Number of live (unexpired) entries. Exposed for diagnostics and tests.</summary>
    int Count { get; }
}
