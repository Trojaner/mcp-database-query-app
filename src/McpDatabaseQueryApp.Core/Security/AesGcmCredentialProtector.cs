using System.Security.Cryptography;
using System.Text;

namespace McpDatabaseQueryApp.Core.Security;

/// <summary>
/// AES-256-GCM <see cref="ICredentialProtector"/> implementation. Encrypts
/// short secrets (typically database passwords) at rest with an authenticated
/// cipher and a fresh 96-bit nonce per call. The output blob is
/// <c>ciphertext || tag</c>, with the nonce returned alongside it so callers
/// can store the two as separate columns.
/// </summary>
/// <remarks>
/// Supports two key sources:
/// <list type="bullet">
///   <item><description>An <see cref="IMasterKeyProvider"/> for the historical
///   master-key-only mode (used when no profile system is present, e.g. tests).</description></item>
///   <item><description>An explicit 32-byte key, supplied by callers that have
///   already derived a per-profile key via <see cref="IProfileKeyProvider"/>.</description></item>
/// </list>
/// </remarks>
public sealed class AesGcmCredentialProtector : ICredentialProtector, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    /// <summary>
    /// Creates a protector keyed from the configured master key. Retained for
    /// backwards compatibility with code paths that have not been migrated to
    /// per-profile derivation.
    /// </summary>
    public AesGcmCredentialProtector(IMasterKeyProvider keyProvider)
    {
        ArgumentNullException.ThrowIfNull(keyProvider);
        _key = keyProvider.GetKey();
        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Master key must be 32 bytes (AES-256).");
        }
    }

    /// <summary>
    /// Creates a protector from an explicit 32-byte key. The caller transfers
    /// ownership of <paramref name="key"/> to this instance, which zeroes it
    /// on dispose.
    /// </summary>
    /// <param name="key">A 32-byte AES-256 key. The byte array is copied
    /// internally so callers may zero their own buffer immediately after this call.</param>
    public AesGcmCredentialProtector(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-GCM key must be 32 bytes.", nameof(key));
        }

        _key = new byte[key.Length];
        Buffer.BlockCopy(key, 0, _key, 0, key.Length);
    }

    /// <inheritdoc/>
    public (byte[] Cipher, byte[] Nonce) Encrypt(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipher = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);

        var combined = new byte[cipher.Length + TagSize];
        Buffer.BlockCopy(cipher, 0, combined, 0, cipher.Length);
        Buffer.BlockCopy(tag, 0, combined, cipher.Length, TagSize);
        return (combined, nonce);
    }

    /// <inheritdoc/>
    public string Decrypt(byte[] cipher, byte[] nonce)
    {
        ArgumentNullException.ThrowIfNull(cipher);
        ArgumentNullException.ThrowIfNull(nonce);
        if (nonce.Length != NonceSize)
        {
            throw new ArgumentException("Unexpected nonce length.", nameof(nonce));
        }

        if (cipher.Length < TagSize)
        {
            throw new ArgumentException("Cipher too short.", nameof(cipher));
        }

        var ctLen = cipher.Length - TagSize;
        var ct = new byte[ctLen];
        var tag = new byte[TagSize];
        Buffer.BlockCopy(cipher, 0, ct, 0, ctLen);
        Buffer.BlockCopy(cipher, ctLen, tag, 0, TagSize);

        var plain = new byte[ctLen];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, ct, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
    }
}
