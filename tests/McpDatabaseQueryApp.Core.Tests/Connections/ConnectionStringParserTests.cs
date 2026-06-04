using FluentAssertions;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Connections;

public sealed class ConnectionStringParserTests
{
    [Fact]
    public void Parses_postgres_keywords_and_infers_provider_from_host()
    {
        var parsed = ConnectionStringParser.Parse(
            "Host=db.internal;Port=5434;Database=etsy_listings;User ID=etsy;Password=etsy_secret;SSL Mode=Disable");

        parsed.InferredKind.Should().Be(DatabaseKind.Postgres);
        parsed.Host.Should().Be("db.internal");
        parsed.Port.Should().Be(5434);
        parsed.Database.Should().Be("etsy_listings");
        parsed.Username.Should().Be("etsy");
        parsed.Password.Should().Be("etsy_secret");
        parsed.SslMode.Should().Be("Disable");
    }

    [Fact]
    public void Infers_sqlserver_from_server_keyword_and_splits_embedded_port()
    {
        var parsed = ConnectionStringParser.Parse(
            "Server=sql.internal,1433;Initial Catalog=app;User Id=sa;Password=p;Encrypt=false;TrustServerCertificate=true");

        parsed.InferredKind.Should().Be(DatabaseKind.SqlServer);
        parsed.Host.Should().Be("sql.internal");
        parsed.Port.Should().Be(1433);
        parsed.Database.Should().Be("app");
        parsed.Username.Should().Be("sa");
        parsed.SslMode.Should().Be("Disable");
        parsed.TrustServerCertificate.Should().BeTrue();
    }

    [Fact]
    public void Data_source_without_host_infers_sqlserver()
    {
        var parsed = ConnectionStringParser.Parse("Data Source=.;Initial Catalog=app;User Id=sa;Password=p");
        parsed.InferredKind.Should().Be(DatabaseKind.SqlServer);
    }

    [Fact]
    public void Ambiguous_string_leaves_inferred_kind_null()
    {
        var parsed = ConnectionStringParser.Parse("Database=app;User ID=u;Password=p");
        parsed.InferredKind.Should().BeNull();
    }

    [Fact]
    public void Sqlserver_encrypt_strict_maps_to_strict()
    {
        var parsed = ConnectionStringParser.Parse("Server=s;Database=d;User Id=u;Password=p;Encrypt=Strict");
        parsed.SslMode.Should().Be("Strict");
    }

    [Fact]
    public void Keys_are_case_and_space_insensitive()
    {
        var parsed = ConnectionStringParser.Parse("host=h;DATABASE=d;userid=u;PWD=secret");
        parsed.Host.Should().Be("h");
        parsed.Database.Should().Be("d");
        parsed.Username.Should().Be("u");
        parsed.Password.Should().Be("secret");
    }
}
