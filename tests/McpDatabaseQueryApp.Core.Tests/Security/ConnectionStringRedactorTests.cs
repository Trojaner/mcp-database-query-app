using McpDatabaseQueryApp.Core.Security;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Security;

public sealed class ConnectionStringRedactorTests
{
    [Theory]
    [InlineData("Host=localhost;Password=hunter2;Database=app", "Password=***")]
    [InlineData("Server=.;User ID=sa;Password=p@ss;", "Password=***")]
    [InlineData("Host=h;pwd=secret;", "pwd=***")]
    [InlineData("Host=h;Api Key=abc;", "Api Key=***")]
    [InlineData("Host=h;AccountKey=xyz;", "AccountKey=***")]
    public void Redacts_known_secret_keys(string input, string expectedFragment)
    {
        var redacted = ConnectionStringRedactor.Redact(input);
        redacted.Should().Contain(expectedFragment);
        redacted.Should().NotContain("hunter2");
        redacted.Should().NotContain("p@ss");
        redacted.Should().NotContain("secret");
        redacted.Should().NotContain("abc");
        redacted.Should().NotContain("xyz");
    }

    [Fact]
    public void Preserves_non_secret_keys()
    {
        var redacted = ConnectionStringRedactor.Redact("Host=localhost;Database=app;Port=5432;Password=hunter2");
        redacted.Should().Contain("Host=localhost").And.Contain("Database=app").And.Contain("Port=5432");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Handles_empty_input(string? input)
    {
        ConnectionStringRedactor.Redact(input).Should().BeEmpty();
    }
}
