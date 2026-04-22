using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Results;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Results;

public sealed class ResultLimiterTests
{
    private static ResultLimiter Create(bool allowDisable = true, int defaultLimit = 500, int maxLimit = 50_000)
    {
        var options = new McpDatabaseQueryAppOptions
        {
            DefaultResultLimit = defaultLimit,
            MaxResultLimit = maxLimit,
            AllowDisableLimit = allowDisable,
        };
        return new ResultLimiter(options);
    }

    [Fact]
    public void Defaults_to_configured_default_when_null()
    {
        var limiter = Create(defaultLimit: 200);
        limiter.Resolve(null, confirmedUnlimited: false).Should().Be(200);
    }

    [Fact]
    public void Caps_at_max_limit()
    {
        var limiter = Create(maxLimit: 1_000);
        limiter.Resolve(5_000, confirmedUnlimited: false).Should().Be(1_000);
    }

    [Fact]
    public void Respects_explicit_limit_below_max()
    {
        var limiter = Create(maxLimit: 1_000);
        limiter.Resolve(200, confirmedUnlimited: false).Should().Be(200);
    }

    [Fact]
    public void Unlimited_requires_confirmation()
    {
        var limiter = Create();
        Action act = () => limiter.Resolve(0, confirmedUnlimited: false);
        act.Should().Throw<UnconfirmedUnlimitedResultException>();
    }

    [Fact]
    public void Unlimited_returns_maxvalue_when_confirmed()
    {
        var limiter = Create();
        limiter.Resolve(0, confirmedUnlimited: true).Should().Be(int.MaxValue);
    }

    [Fact]
    public void Unlimited_throws_when_disabled_by_config()
    {
        var limiter = Create(allowDisable: false);
        Action act = () => limiter.Resolve(0, confirmedUnlimited: true);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Negative_limit_is_rejected()
    {
        var limiter = Create();
        Action act = () => limiter.Resolve(-1, confirmedUnlimited: false);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(500, false)]
    [InlineData(null, false)]
    public void IsUnlimited_returns_expected(int? requested, bool expected)
    {
        Create().IsUnlimited(requested).Should().Be(expected);
    }
}
