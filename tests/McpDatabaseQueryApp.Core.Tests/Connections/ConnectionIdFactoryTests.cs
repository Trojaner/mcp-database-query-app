using McpDatabaseQueryApp.Core.Connections;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Connections;

public sealed class ConnectionIdFactoryTests
{
    [Fact]
    public void NewConnectionId_has_prefix_and_hex()
    {
        var id = ConnectionIdFactory.NewConnectionId();
        id.Should().StartWith("conn_");
        id.Length.Should().Be("conn_".Length + 12);
    }

    [Fact]
    public void Ids_are_unique()
    {
        var ids = Enumerable.Range(0, 1_000).Select(_ => ConnectionIdFactory.NewConnectionId()).ToHashSet();
        ids.Count.Should().Be(1_000);
    }

    [Theory]
    [InlineData("db_")]
    [InlineData("script_")]
    [InlineData("result_")]
    public void Category_factories_have_expected_prefix(string prefix)
    {
        var id = prefix switch
        {
            "db_" => ConnectionIdFactory.NewDatabaseId(),
            "script_" => ConnectionIdFactory.NewScriptId(),
            "result_" => ConnectionIdFactory.NewResultSetId(),
            _ => throw new ArgumentOutOfRangeException(nameof(prefix)),
        };
        id.Should().StartWith(prefix);
    }
}
