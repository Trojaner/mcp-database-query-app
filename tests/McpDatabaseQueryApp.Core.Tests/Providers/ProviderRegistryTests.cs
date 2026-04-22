using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Providers;

public sealed class ProviderRegistryTests
{
    [Fact]
    public void Get_returns_registered_provider()
    {
        var registry = new ProviderRegistry(new[] { (IDatabaseProvider)new FakeProvider(DatabaseKind.Postgres) });
        registry.Get(DatabaseKind.Postgres).Should().NotBeNull();
    }

    [Fact]
    public void Get_throws_for_unknown()
    {
        var registry = new ProviderRegistry(new[] { (IDatabaseProvider)new FakeProvider(DatabaseKind.Postgres) });
        Action act = () => registry.Get(DatabaseKind.SqlServer);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("Postgres", true)]
    [InlineData("postgres", true)]
    [InlineData("POSTGRES", true)]
    [InlineData("SqlServer", false)]
    [InlineData("unknown", false)]
    public void TryGet_is_case_insensitive(string name, bool expected)
    {
        var registry = new ProviderRegistry(new[] { (IDatabaseProvider)new FakeProvider(DatabaseKind.Postgres) });
        registry.TryGet(name, out _).Should().Be(expected);
    }

    [Fact]
    public void All_exposes_all_registered_providers()
    {
        var registry = new ProviderRegistry(new IDatabaseProvider[]
        {
            new FakeProvider(DatabaseKind.Postgres),
            new FakeProvider(DatabaseKind.SqlServer),
        });
        registry.All.Should().HaveCount(2);
    }

    private sealed class FakeProvider : IDatabaseProvider
    {
        public FakeProvider(DatabaseKind kind) { Kind = kind; }
        public DatabaseKind Kind { get; }
        public ProviderCapabilities Capabilities { get; } = new(false, false, false, false, false, false, [], []);
        public string BuildConnectionString(ConnectionDescriptor descriptor, string password) => string.Empty;
        public Task<IDatabaseConnection> OpenAsync(ConnectionDescriptor descriptor, string password, CancellationToken ct, string? preassignedConnectionId = null) => throw new NotSupportedException();
    }
}
