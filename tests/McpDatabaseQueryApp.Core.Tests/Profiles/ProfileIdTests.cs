using FluentAssertions;
using McpDatabaseQueryApp.Core.Profiles;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Profiles;

public sealed class ProfileIdTests
{
    [Fact]
    public void Default_constant_round_trips()
    {
        ProfileId.Default.Value.Should().Be(ProfileId.DefaultValue);
        ProfileId.Default.IsDefault.Should().BeTrue();
        new ProfileId(ProfileId.DefaultValue).IsDefault.Should().BeTrue();
    }

    [Fact]
    public void Equality_uses_value()
    {
        var a = new ProfileId("alice");
        var b = new ProfileId("alice");
        var c = new ProfileId("bob");

        a.Should().Be(b);
        a.Equals((object)b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
        a.Should().NotBe(c);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Constructor_rejects_empty_or_whitespace(string value)
    {
        Action act = () => _ = new ProfileId(value);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_rejects_null()
    {
        Action act = () => _ = new ProfileId(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ToString_returns_value()
    {
        new ProfileId("p_abc").ToString().Should().Be("p_abc");
    }

    [Fact]
    public void Non_default_id_is_not_default()
    {
        new ProfileId("alice").IsDefault.Should().BeFalse();
    }
}
