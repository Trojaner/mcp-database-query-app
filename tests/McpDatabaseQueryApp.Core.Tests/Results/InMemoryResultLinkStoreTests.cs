using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Results;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Results;

public sealed class InMemoryResultLinkStoreTests
{
    private static (InMemoryResultLinkStore Store, TestClock Clock) Create(
        Action<ResultLinkOptions>? configure = null)
    {
        var options = new McpDatabaseQueryAppOptions();
        configure?.Invoke(options.ResultLinks);
        var clock = new TestClock(DateTimeOffset.Parse("2026-09-08T10:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
        return (new InMemoryResultLinkStore(options, NullLogger<InMemoryResultLinkStore>.Instance, clock), clock);
    }

    /// <summary>Minimal manually-advanced clock; the store only ever asks for "now".</summary>
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now;

        public TestClock(DateTimeOffset start) => _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    [Fact]
    public void Resolves_the_stored_payload_for_a_freshly_minted_token()
    {
        var (store, _) = Create();

        var link = store.Create("{\"rows\":[]}", "application/json; charset=utf-8");
        var content = store.Resolve(link.Token);

        content.Should().NotBeNull();
        content!.Body.Should().Be("{\"rows\":[]}");
        content.ContentType.Should().Be("application/json; charset=utf-8");
        link.Token.Should().StartWith(link.Id);
    }

    [Fact]
    public void Defaults_to_a_two_hour_lifetime()
    {
        var (store, clock) = Create();

        var link = store.Create("{}", "application/json");

        (link.ExpiresAt - clock.GetUtcNow()).Should().Be(TimeSpan.FromHours(2));
    }

    [Fact]
    public void Stops_resolving_once_the_ttl_has_passed()
    {
        var (store, clock) = Create(o => o.Ttl = TimeSpan.FromMinutes(30));
        var link = store.Create("{}", "application/json");

        clock.Advance(TimeSpan.FromMinutes(29));
        store.Resolve(link.Token).Should().NotBeNull();

        clock.Advance(TimeSpan.FromMinutes(2));
        store.Resolve(link.Token).Should().BeNull();
    }

    [Fact]
    public void Refuses_a_token_whose_secret_half_was_tampered_with()
    {
        var (store, _) = Create();
        var link = store.Create("{\"secret\":true}", "application/json");

        // Flip the last character of the secret while keeping the lookup id
        // intact — the id alone must not be enough to read the payload.
        var last = link.Token[^1];
        var tampered = link.Token[..^1] + (last == 'A' ? 'B' : 'A');

        store.Resolve(tampered).Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void Refuses_unknown_and_malformed_tokens(string token)
    {
        var (store, _) = Create();
        store.Create("{}", "application/json");

        store.Resolve(token).Should().BeNull();
    }

    [Fact]
    public void Mints_a_distinct_token_for_every_call()
    {
        var (store, _) = Create();

        var tokens = Enumerable.Range(0, 50).Select(_ => store.Create("{}", "application/json").Token).ToList();

        tokens.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Evicts_the_entry_closest_to_expiry_once_max_entries_is_reached()
    {
        var (store, clock) = Create(o =>
        {
            o.MaxEntries = 3;
            o.Ttl = TimeSpan.FromHours(2);
        });

        var first = store.Create("{\"n\":1}", "application/json");
        clock.Advance(TimeSpan.FromMinutes(1));
        var second = store.Create("{\"n\":2}", "application/json");
        clock.Advance(TimeSpan.FromMinutes(1));
        var third = store.Create("{\"n\":3}", "application/json");

        store.Resolve(first.Token).Should().NotBeNull();

        // The cap is reached, so storing a fourth drops the oldest.
        clock.Advance(TimeSpan.FromMinutes(1));
        var fourth = store.Create("{\"n\":4}", "application/json");

        store.Resolve(first.Token).Should().BeNull();
        store.Resolve(second.Token).Should().NotBeNull();
        store.Resolve(third.Token).Should().NotBeNull();
        store.Resolve(fourth.Token).Should().NotBeNull();
        store.Count.Should().Be(3);
    }

    [Fact]
    public void Count_ignores_entries_that_have_already_expired()
    {
        var (store, clock) = Create(o => o.Ttl = TimeSpan.FromMinutes(10));
        store.Create("{}", "application/json");

        store.Count.Should().Be(1);
        clock.Advance(TimeSpan.FromMinutes(11));
        store.Count.Should().Be(0);
    }
}
