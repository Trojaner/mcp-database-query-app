using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using McpDatabaseQueryApp.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Core.Results;

/// <summary>
/// Process-local <see cref="IResultLinkStore"/>. Entries live in memory only,
/// so every link dies with the process — the tool surface documents this as
/// "cached until the TTL expires or the server restarts".
/// </summary>
/// <remarks>
/// <para>
/// A token is <c>id || secret</c>, both base64url: an 8-byte lookup id and a
/// 16-byte secret. The dictionary is keyed by the id and stores only the
/// SHA-256 of the secret, so a leaked heap dump does not hand out working
/// links, and verification runs in constant time.
/// </para>
/// <para>
/// Expiry is swept lazily on write (there is no timer): a resolve of an
/// expired entry fails before the payload is touched, and
/// <see cref="ResultLinkOptions.MaxEntries"/> bounds memory by evicting the
/// entries closest to expiry once the cap is reached.
/// </para>
/// </remarks>
public sealed class InMemoryResultLinkStore : IResultLinkStore
{
    private const int IdBytes = 8;
    private const int SecretBytes = 16;

    // base64url of N bytes is ceil(N * 4 / 3) chars with no padding.
    private const int IdChars = 11;
    private const int SecretChars = 22;
    private const int TokenChars = IdChars + SecretChars;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ResultLinkOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<InMemoryResultLinkStore> _logger;

    public InMemoryResultLinkStore(
        McpDatabaseQueryAppOptions options,
        ILogger<InMemoryResultLinkStore> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.ResultLinks;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public int Count
    {
        get
        {
            var now = _time.GetUtcNow();
            var live = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.ExpiresAt > now)
                {
                    live++;
                }
            }

            return live;
        }
    }

    public ResultLink Create(string body, string contentType, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);

        var now = _time.GetUtcNow();
        var lifetime = ttl ?? _options.Ttl;
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ttl), "Result link TTL must be positive.");
        }

        PurgeExpired(now);
        EnforceCapacity();

        var id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(IdBytes));
        var secretBytes = RandomNumberGenerator.GetBytes(SecretBytes);
        var secret = Base64Url.EncodeToString(secretBytes);

        var entry = new Entry(SHA256.HashData(secretBytes), body, contentType, now, now + lifetime);
        while (!_entries.TryAdd(id, entry))
        {
            // 64 bits of id: a collision is a lottery win, but retry rather than clobber.
            id = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(IdBytes));
        }

        _logger.LogDebug(
            "Stored result link {ResultLinkId} ({Bytes} bytes) expiring at {ExpiresAt:o}.",
            id,
            body.Length,
            entry.ExpiresAt);

        return new ResultLink(id, id + secret, entry.CreatedAt, entry.ExpiresAt);
    }

    public ResultLinkContent? Resolve(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != TokenChars)
        {
            return null;
        }

        var id = token[..IdChars];
        if (!_entries.TryGetValue(id, out var entry))
        {
            return null;
        }

        var now = _time.GetUtcNow();
        if (entry.ExpiresAt <= now)
        {
            _entries.TryRemove(new KeyValuePair<string, Entry>(id, entry));
            return null;
        }

        Span<byte> secretBytes = stackalloc byte[SecretBytes];
        if (!Base64Url.TryDecodeFromChars(token.AsSpan(IdChars), secretBytes, out var written) || written != SecretBytes)
        {
            return null;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(secretBytes, hash);
        if (!CryptographicOperations.FixedTimeEquals(hash, entry.SecretHash))
        {
            _logger.LogWarning("Result link {ResultLinkId} was requested with an invalid token.", id);
            return null;
        }

        return new ResultLinkContent(entry.Body, entry.ContentType, entry.CreatedAt, entry.ExpiresAt);
    }

    private void PurgeExpired(DateTimeOffset now)
    {
        foreach (var pair in _entries)
        {
            if (pair.Value.ExpiresAt <= now)
            {
                _entries.TryRemove(pair);
            }
        }
    }

    private void EnforceCapacity()
    {
        var max = _options.MaxEntries;
        if (max <= 0)
        {
            return;
        }

        while (_entries.Count >= max)
        {
            KeyValuePair<string, Entry>? oldest = null;
            foreach (var pair in _entries)
            {
                if (oldest is null || pair.Value.ExpiresAt < oldest.Value.Value.ExpiresAt)
                {
                    oldest = pair;
                }
            }

            if (oldest is null || !_entries.TryRemove(oldest.Value))
            {
                // Another thread already drained it; re-check the count.
                if (_entries.Count < max)
                {
                    return;
                }

                continue;
            }

            _logger.LogDebug(
                "Evicted result link {ResultLinkId} to stay within MaxEntries={MaxEntries}.",
                oldest.Value.Key,
                max);
        }
    }

    private sealed record Entry(
        byte[] SecretHash,
        string Body,
        string ContentType,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}
