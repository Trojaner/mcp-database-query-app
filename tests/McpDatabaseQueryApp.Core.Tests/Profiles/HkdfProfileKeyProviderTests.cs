using FluentAssertions;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Security;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Profiles;

public sealed class HkdfProfileKeyProviderTests
{
    [Fact]
    public void Same_profile_id_produces_same_key()
    {
        var provider = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 1));

        var k1 = provider.DeriveKey(new ProfileId("alice"));
        var k2 = provider.DeriveKey(new ProfileId("alice"));

        k1.Should().BeEquivalentTo(k2);
    }

    [Fact]
    public void Different_profile_ids_produce_different_keys()
    {
        var provider = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 1));

        var k1 = provider.DeriveKey(new ProfileId("alice"));
        var k2 = provider.DeriveKey(new ProfileId("bob"));

        k1.Should().NotBeEquivalentTo(k2);
    }

    [Fact]
    public void Derived_key_is_32_bytes()
    {
        var provider = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 1));

        var key = provider.DeriveKey(ProfileId.Default);

        key.Should().HaveCount(32);
    }

    [Fact]
    public void Different_master_keys_produce_different_derived_keys_for_same_profile()
    {
        var providerA = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 1));
        var providerB = new HkdfProfileKeyProvider(new FixedMasterKey(seed: 99));

        var ka = providerA.DeriveKey(new ProfileId("alice"));
        var kb = providerB.DeriveKey(new ProfileId("alice"));

        ka.Should().NotBeEquivalentTo(kb);
    }

    [Fact]
    public void Throws_when_master_key_is_not_32_bytes()
    {
        var provider = new HkdfProfileKeyProvider(new ShortMasterKey());

        Action act = () => provider.DeriveKey(ProfileId.Default);

        act.Should().Throw<InvalidOperationException>();
    }

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

    private sealed class ShortMasterKey : IMasterKeyProvider
    {
        public byte[] GetKey() => new byte[16];
    }
}
