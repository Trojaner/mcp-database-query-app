using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpDatabaseQueryApp.Providers.SqlServer.Tests;

[Collection("mssql")]
public sealed class SqlServerProviderTests
{
    private readonly SqlServerFixture _fixture;

    public SqlServerProviderTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDatabaseConnection> OpenAsync(bool readOnly = false)
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker is not available for SQL Server tests.");
        var provider = new SqlServerProvider(NullLoggerFactory.Instance);
        var descriptor = new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = "mssql-test",
            Provider = DatabaseKind.SqlServer,
            Host = _fixture.Host,
            Port = _fixture.Port,
            Database = _fixture.Database,
            Username = _fixture.Username,
            SslMode = "Optional",
            TrustServerCertificate = true,
            ReadOnly = readOnly,
        };
        return await provider.OpenAsync(descriptor, _fixture.Password, CancellationToken.None);
    }

    [SkippableFact]
    public async Task Ping_succeeds()
    {
        await using var conn = await OpenAsync();
        await conn.PingAsync(CancellationToken.None);
    }

    [SkippableFact]
    public async Task Query_returns_rows()
    {
        await using var conn = await OpenAsync();
        var result = await conn.ExecuteQueryAsync(
            new QueryRequest("SELECT 1 AS n, N'abc' AS s", null, null, null),
            CancellationToken.None);
        result.Columns.Should().HaveCount(2);
        result.Rows[0][0].Should().Be(1);
        result.Rows[0][1].Should().Be("abc");
    }

    [SkippableFact]
    public async Task Parameterised_query_passes_arguments()
    {
        await using var conn = await OpenAsync();
        var result = await conn.ExecuteQueryAsync(
            new QueryRequest("SELECT @x + @y", new Dictionary<string, object?> { ["x"] = 2, ["y"] = 3 }, null, null),
            CancellationToken.None);
        result.Rows[0][0].Should().Be(5);
    }

    [SkippableFact]
    public async Task Create_and_describe_table()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteNonQueryAsync(new NonQueryRequest(
            "IF OBJECT_ID('dbo.mdqa_users', 'U') IS NOT NULL DROP TABLE dbo.mdqa_users; CREATE TABLE dbo.mdqa_users (id int IDENTITY PRIMARY KEY, email nvarchar(200) NOT NULL);",
            null, null), CancellationToken.None);

        var details = await conn.DescribeTableAsync("dbo", "mdqa_users", CancellationToken.None);
        details.Columns.Should().HaveCount(2);
        details.Columns.Should().Contain(c => c.Name == "id" && c.IsIdentity && c.IsPrimaryKey);
        details.Columns.Should().Contain(c => c.Name == "email" && !c.IsNullable);
    }

    [SkippableFact]
    public async Task List_schemas_returns_dbo()
    {
        await using var conn = await OpenAsync();
        var schemas = await conn.ListSchemasAsync(CancellationToken.None);
        schemas.Should().Contain(s => s.Name == "dbo");
    }

    [SkippableFact]
    public async Task List_databases_includes_master()
    {
        await using var conn = await OpenAsync();
        var dbs = await conn.ListDatabasesAsync(CancellationToken.None);
        dbs.Should().Contain(d => d.Name == "master");
    }

    [SkippableFact]
    public async Task Limit_truncates_and_reports_total()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteNonQueryAsync(new NonQueryRequest(
            "IF OBJECT_ID('dbo.mdqa_numbers', 'U') IS NOT NULL DROP TABLE dbo.mdqa_numbers; CREATE TABLE dbo.mdqa_numbers(n int);",
            null, null), CancellationToken.None);
        for (var i = 0; i < 100; i++)
        {
            await conn.ExecuteNonQueryAsync(new NonQueryRequest("INSERT INTO dbo.mdqa_numbers VALUES (@n)", new Dictionary<string, object?> { ["n"] = i }, null), CancellationToken.None);
        }

        var result = await conn.ExecuteQueryAsync(
            new QueryRequest("SELECT n FROM dbo.mdqa_numbers ORDER BY n", null, Limit: 10, null),
            CancellationToken.None);

        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeTrue();
        result.TotalRowsAvailable.Should().Be(100);
    }

    [SkippableFact]
    public async Task ReadOnly_connection_blocks_writes()
    {
        await using var conn = await OpenAsync(readOnly: true);
        Func<Task> act = async () => await conn.ExecuteNonQueryAsync(
            new NonQueryRequest("CREATE TABLE dbo.should_not_exist (id int);", null, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task Connection_string_never_leaks_password_when_redacted()
    {
        var provider = new SqlServerProvider(NullLoggerFactory.Instance);
        var descriptor = new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = "mssql-test",
            Provider = DatabaseKind.SqlServer,
            Host = "example.com",
            Database = "app",
            Username = "sa",
            SslMode = "Mandatory",
            TrustServerCertificate = true,
        };
        var connString = provider.BuildConnectionString(descriptor, "hunter2!!!");
        connString.Should().Contain("hunter2!!!");
        McpDatabaseQueryApp.Core.Security.ConnectionStringRedactor.Redact(connString).Should().NotContain("hunter2");
    }
}
