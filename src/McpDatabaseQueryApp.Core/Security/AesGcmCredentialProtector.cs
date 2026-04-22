using System.Security.Cryptography;
using System.Text;

namespace McpDatabaseQueryApp.Core.Security;

public sealed class AesGcmCredentialProtector : ICredentialProtector, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;

    public AesGcmCredentialProtector(IMasterKeyProvider keyProvider)
    {
        ArgumentNullException.ThrowIfNull(keyProvider);
        _key = keyProvider.GetKey();
        if (_key.Length != 32)
        {
            throw new InvalidOperationException("Master key must be 32 bytes (AES-256).");
        }
    }

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

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(_key);
    }
}
