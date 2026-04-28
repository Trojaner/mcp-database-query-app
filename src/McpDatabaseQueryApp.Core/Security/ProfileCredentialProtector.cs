using System.Collections.Concurrent;
using System.Security.Cryptography;
using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Security;

/// <summary>
/// Default <see cref="IProfileCredentialProtector"/>. Caches one
/// <see cref="AesGcmCredentialProtector"/> per profile id, derived from the
/// shared <see cref="IProfileKeyProvider"/>.
/// </summary>
public sealed class ProfileCredentialProtector : IProfileCredentialProtector, IDisposable
{
    private readonly IProfileKeyProvider _keyProvider;
    private readonly ConcurrentDictionary<string, AesGcmCredentialProtector> _cache = new(StringComparer.Ordinal);
    private int _disposed;

    /// <summary>
    /// Creates a new <see cref="ProfileCredentialProtector"/>.
    /// </summary>
    public ProfileCredentialProtector(IProfileKeyProvider keyProvider)
    {
        ArgumentNullException.ThrowIfNull(keyProvider);
        _keyProvider = keyProvider;
    }

    /// <inheritdoc/>
    public (byte[] Cipher, byte[] Nonce) Protect(ProfileId profileId, string plaintext)
        => GetOrCreate(profileId).Encrypt(plaintext);

    /// <inheritdoc/>
    public string Unprotect(ProfileId profileId, byte[] cipher, byte[] nonce)
        => GetOrCreate(profileId).Decrypt(cipher, nonce);

    private AesGcmCredentialProtector GetOrCreate(ProfileId profileId)
    {
        if (_cache.TryGetValue(profileId.Value, out var cached))
        {
            return cached;
        }

        var key = _keyProvider.DeriveKey(profileId);
        try
        {
            var fresh = new AesGcmCredentialProtector(key);
            if (_cache.TryAdd(profileId.Value, fresh))
            {
                return fresh;
            }

            // Lost a race; dispose our copy and use the cached one.
            fresh.Dispose();
            return _cache[profileId.Value];
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var protector in _cache.Values)
        {
            protector.Dispose();
        }

        _cache.Clear();
    }
}
