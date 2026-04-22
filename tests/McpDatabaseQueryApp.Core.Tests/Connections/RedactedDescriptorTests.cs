using System.Text.Json;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Connections;

public sealed class RedactedDescriptorTests
{
    [Fact]
    public void From_copies_metadata_and_never_carries_password()
    {
        var descriptor = new ConnectionDescriptor
        {
            Id = "db_xxxxxx",
            Name = "analytics",
            Provider = DatabaseKind.Postgres,
            Host = "db.example.com",
            Port = 5432,
            Database = "analytics",
            Username = "reader",
            SslMode = "Require",
            TrustServerCertificate = false,
            ReadOnly = true,
            DefaultSchema = "public",
            Tags = new[] { "prod" },
        };

        var redacted = RedactedDescriptor.From(descriptor);
        var json = JsonSerializer.Serialize(redacted);

        json.Should().NotContain("password", because: "passwords must never be serialized");
        json.Should().NotContain("Password");
        redacted.Name.Should().Be("analytics");
        redacted.Provider.Should().Be("Postgres");
        redacted.Host.Should().Be("db.example.com");
        redacted.Username.Should().Be("reader");
        redacted.ReadOnly.Should().BeTrue();
        redacted.Tags.Should().ContainSingle().Which.Should().Be("prod");
    }

    [Fact]
    public void Descriptor_type_has_no_password_member()
    {
        typeof(ConnectionDescriptor).GetProperties()
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("password", StringComparison.OrdinalIgnoreCase));
    }
}
