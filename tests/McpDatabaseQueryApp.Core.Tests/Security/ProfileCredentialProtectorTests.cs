using System.Security.Cryptography;
using FluentAssertions;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Security;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Security;

public sealed class ProfileCredentialProtectorTests
{
    [Fact]
    public void Round_trip_within_same_profile_succeeds()
    {
        var keys = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 7));
        using var protector = new ProfileCredentialProtector(keys);
        var profile = new ProfileId("alice");

        var (cipher, nonce) = protector.Protect(profile, "hunter2");
        var plaintext = protector.Unprotect(profile, cipher, nonce);

        plaintext.Should().Be("hunter2");
    }

    [Fact]
    public void Profile_a_blob_cannot_be_unprotected_with_profile_b()
    {
        var keys = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 7));
        using var protector = new ProfileCredentialProtector(keys);

        var (cipher, nonce) = protector.Protect(new ProfileId("alice"), "hunter2");

        Action act = () => protector.Unprotect(new ProfileId("bob"), cipher, nonce);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void Master_key_change_invalidates_old_blobs()
    {
        var (cipher, nonce) = ProtectWithMasterSeed("alice", "hunter2", seed: 7);

        var freshKeys = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 99));
        using var freshProtector = new ProfileCredentialProtector(freshKeys);

        Action act = () => freshProtector.Unprotect(new ProfileId("alice"), cipher, nonce);

        act.Should().Throw<CryptographicException>();
    }

    [Fact]
    public void AmbientProfileCredentialProtector_uses_current_profile()
    {
        var keys = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 7));
        using var inner = new ProfileCredentialProtector(keys);
        var accessor = new ProfileContextAccessor();
        var ambient = new AmbientProfileCredentialProtector(inner, accessor);

        var alice = MakeProfile("alice");
        var bob = MakeProfile("bob");

        byte[] cipher;
        byte[] nonce;
        using (accessor.Begin(alice))
        {
            (cipher, nonce) = ambient.Encrypt("hunter2");
        }

        // Decrypt under a *different* ambient profile must fail.
        using (accessor.Begin(bob))
        {
            Action act = () => ambient.Decrypt(cipher, nonce);
            act.Should().Throw<CryptographicException>();
        }

        // But re-decrypt under alice succeeds.
        using (accessor.Begin(alice))
        {
            ambient.Decrypt(cipher, nonce).Should().Be("hunter2");
        }
    }

    private static (byte[] cipher, byte[] nonce) ProtectWithMasterSeed(string profileId, string plaintext, int seed)
    {
        var keys = new HkdfProfileKeyProvider(new FixedMasterKey(seed));
        using var protector = new ProfileCredentialProtector(keys);
        return protector.Protect(new ProfileId(profileId), plaintext);
    }

    private static Profile MakeProfile(string id) => new(
        new ProfileId(id),
        Name: id,
        Subject: id,
        Issuer: null,
        CreatedAt: DateTimeOffset.UtcNow,
        Status: ProfileStatus.Active,
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal));

    private sealed class FixedMasterKey : IMasterKeyProvider
    {
        private readonly int _seed;

        public FixedMasterKey(int seed) => _seed = seed;

        public byte[] GetKey()
        {
            var key = new byte[32];
            for (var i = 0; i < key.Length; i++)
            {
                key[i] = (byte)((i + _seed) & 0xFF);
            }
            return key;
        }
    }
}
