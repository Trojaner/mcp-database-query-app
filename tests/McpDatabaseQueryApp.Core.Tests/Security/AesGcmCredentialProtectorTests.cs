using System.Security.Cryptography;
using McpDatabaseQueryApp.Core.Security;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Security;

public sealed class AesGcmCredentialProtectorTests
{
    [Fact]
    public void Roundtrip_preserves_plaintext()
    {
        using var protector = new AesGcmCredentialProtector(new TestKeyProvider());
        var (cipher, nonce) = protector.Encrypt("correct horse battery staple");

        var decrypted = protector.Decrypt(cipher, nonce);

        decrypted.Should().Be("correct horse battery staple");
    }

    [Fact]
    public void Encrypt_produces_unique_nonces()
    {
        using var protector = new AesGcmCredentialProtector(new TestKeyProvider());

        var (_, n1) = protector.Encrypt("password1");
        var (_, n2) = protector.Encrypt("password1");

        n1.Should().NotBeEquivalentTo(n2);
    }

    [Fact]
    public void Encrypt_produces_unique_ciphertexts_for_same_plaintext()
    {
        using var protector = new AesGcmCredentialProtector(new TestKeyProvider());

        var (c1, _) = protector.Encrypt("password1");
        var (c2, _) = protector.Encrypt("password1");

        c1.Should().NotBeEquivalentTo(c2);
    }

    [Fact]
    public void Decrypt_with_tampered_cipher_throws()
    {
        using var protector = new AesGcmCredentialProtector(new TestKeyProvider());
        var (cipher, nonce) = protector.Encrypt("secret");
        cipher[0] ^= 0xFF;

        Action act = () => protector.Decrypt(cipher, nonce);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Decrypt_with_wrong_key_throws()
    {
        using var alice = new AesGcmCredentialProtector(new TestKeyProvider(seed: 1));
        using var bob = new AesGcmCredentialProtector(new TestKeyProvider(seed: 2));
        var (cipher, nonce) = alice.Encrypt("secret");

        Action act = () => bob.Decrypt(cipher, nonce);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Constructor_rejects_short_key()
    {
        Action act = () => _ = new AesGcmCredentialProtector(new ShortKeyProvider());
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("unicode: héllo — 世界")]
    public void Roundtrip_various_plaintexts(string plaintext)
    {
        using var protector = new AesGcmCredentialProtector(new TestKeyProvider());
        var (cipher, nonce) = protector.Encrypt(plaintext);
        protector.Decrypt(cipher, nonce).Should().Be(plaintext);
    }

    private sealed class TestKeyProvider : IMasterKeyProvider
    {
        private readonly byte[] _key;

        public TestKeyProvider(int seed = 42)
        {
            _key = new byte[32];
            for (var i = 0; i < _key.Length; i++)
            {
                _key[i] = (byte)((i + seed) & 0xFF);
            }
        }

        public byte[] GetKey() => _key.ToArray();
    }

    private sealed class ShortKeyProvider : IMasterKeyProvider
    {
        public byte[] GetKey() => new byte[16];
    }
}
