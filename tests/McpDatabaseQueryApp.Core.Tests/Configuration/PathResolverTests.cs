using McpDatabaseQueryApp.Core.Configuration;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Configuration;

public sealed class PathResolverTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Empty_input_is_returned_unchanged(string? input)
    {
        PathResolver.Resolve(input ?? string.Empty).Should().Be(string.Empty);
    }

    [Fact]
    public void Expands_appdata()
    {
        var resolved = PathResolver.Resolve("%APPDATA%/McpDatabaseQueryApp/meta.db");
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        resolved.Should().StartWith(appData);
        resolved.Should().EndWith("meta.db");
    }

    [Fact]
    public void Forward_slashes_are_normalized()
    {
        var resolved = PathResolver.Resolve("/tmp/abc/file.db");
        resolved.Should().NotContain("/", because: "Windows should convert forward slashes");
    }

    [Fact]
    public void Expands_tilde()
    {
        var resolved = PathResolver.Resolve("~/mdqa.db");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        resolved.Should().StartWith(home);
    }
}
