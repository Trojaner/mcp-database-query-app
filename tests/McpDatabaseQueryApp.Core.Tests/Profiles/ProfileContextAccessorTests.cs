using FluentAssertions;
using McpDatabaseQueryApp.Core.Profiles;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Profiles;

public sealed class ProfileContextAccessorTests
{
    [Fact]
    public void Current_is_null_by_default()
    {
        IProfileContextAccessor accessor = new ProfileContextAccessor();

        accessor.Current.Should().BeNull();
        accessor.CurrentIdOrDefault.Should().Be(ProfileId.Default);
    }

    [Fact]
    public void Begin_sets_current_for_scope()
    {
        var accessor = new ProfileContextAccessor();
        var profile = MakeProfile("alice");

        using (accessor.Begin(profile))
        {
            accessor.Current.Should().Be(profile);
        }

        accessor.Current.Should().BeNull();
    }

    [Fact]
    public void Nested_scopes_restore_outer_on_dispose()
    {
        var accessor = new ProfileContextAccessor();
        var outer = MakeProfile("alice");
        var inner = MakeProfile("bob");

        using (accessor.Begin(outer))
        {
            accessor.Current.Should().Be(outer);
            using (accessor.Begin(inner))
            {
                accessor.Current.Should().Be(inner);
            }

            accessor.Current.Should().Be(outer);
        }

        accessor.Current.Should().BeNull();
    }

    [Fact]
    public async Task Parallel_tasks_each_see_their_own_ambient()
    {
        var accessor = new ProfileContextAccessor();
        var alice = MakeProfile("alice");
        var bob = MakeProfile("bob");

        var fromA = Task.Run(async () =>
        {
            using (accessor.Begin(alice))
            {
                await Task.Delay(20);
                return accessor.Current;
            }
        });

        var fromB = Task.Run(async () =>
        {
            using (accessor.Begin(bob))
            {
                await Task.Delay(20);
                return accessor.Current;
            }
        });

        var fromC = Task.Run(() =>
        {
            // No scope opened; the AsyncLocal stack must be empty for this flow.
            return accessor.Current;
        });

        var results = await Task.WhenAll(fromA, fromB, fromC);
        results[0].Should().Be(alice);
        results[1].Should().Be(bob);
        results[2].Should().BeNull();
    }

    [Fact]
    public void Begin_throws_on_null()
    {
        var accessor = new ProfileContextAccessor();
        Action act = () => accessor.Begin(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Double_dispose_is_safe()
    {
        var accessor = new ProfileContextAccessor();
        var scope = accessor.Begin(MakeProfile("alice"));
        scope.Dispose();
        scope.Dispose();
        accessor.Current.Should().BeNull();
    }

    private static Profile MakeProfile(string id) => new(
        new ProfileId(id),
        Name: id,
        Subject: id,
        Issuer: null,
        CreatedAt: DateTimeOffset.UtcNow,
        Status: ProfileStatus.Active,
        Metadata: new Dictionary<string, string>(StringComparer.Ordinal));
}
