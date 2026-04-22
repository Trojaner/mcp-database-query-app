using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpDatabaseQueryApp.Providers.Postgres.Tests;

[Collection("postgres")]
public sealed class PostgresProviderTests
{
    private readonly PostgresFixture _fixture;

    public PostgresProviderTests(PostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<IDatabaseConnection> OpenAsync(bool readOnly = false)
    {
        Skip.IfNot(_fixture.DockerAvailable, "Docker is not available for Postgres tests.");
        var provider = new PostgresProvider(NullLoggerFactory.Instance);
        var descriptor = new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = "pg-test",
            Provider = DatabaseKind.Postgres,
            Host = _fixture.Host,
            Port = _fixture.Port,
            Database = _fixture.Database,
            Username = _fixture.Username,
            SslMode = "Prefer",
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
    public async Task Query_returns_structured_rows()
    {
        await using var conn = await OpenAsync();
        var result = await conn.ExecuteQueryAsync(
            new QueryRequest("SELECT 1 AS n, 'abc' AS s", null, null, null),
            CancellationToken.None);

        result.Columns.Should().HaveCount(2);
        result.Columns[0].Name.Should().Be("n");
        result.Columns[1].Name.Should().Be("s");
        result.Rows.Should().HaveCount(1);
        result.Rows[0][0].Should().Be(1);
        result.Rows[0][1].Should().Be("abc");
    }

    [SkippableFact]
    public async Task Parameterised_query_passes_arguments()
    {
        await using var conn = await OpenAsync();
        var result = await conn.ExecuteQueryAsync(
            new QueryRequest("SELECT @x::int + @y::int AS sum", new Dictionary<string, object?> { ["x"] = 2, ["y"] = 3 }, null, null),
            CancellationToken.None);
        result.Rows[0][0].Should().Be(5);
    }

    [SkippableFact]
    public async Task Limit_truncates_and_reports_total()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteNonQueryAsync(
            new NonQueryRequest("DROP TABLE IF EXISTS mdqa_numbers; CREATE TABLE mdqa_numbers (n int); INSERT INTO mdqa_numbers SELECT generate_series(1, 100);", null, null),
            CancellationToken.None);

        var result = await conn.ExecuteQueryAsync(
            new QueryRequest("SELECT n FROM mdqa_numbers ORDER BY n", null, Limit: 10, null),
            CancellationToken.None);

        result.Rows.Should().HaveCount(10);
        result.Truncated.Should().BeTrue();
        result.TotalRowsAvailable.Should().Be(100);
    }

    [SkippableFact]
    public async Task ReadOnly_connection_blocks_writes()
    {
        await using var conn = await OpenAsync(readOnly: true);
        await conn.ExecuteQueryAsync(new QueryRequest("SELECT 1", null, null, null), CancellationToken.None);

        Func<Task> act = async () => await conn.ExecuteNonQueryAsync(
            new NonQueryRequest("CREATE TABLE should_not_exist (id int);", null, null),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [SkippableFact]
    public async Task List_schemas_returns_public()
    {
        await using var conn = await OpenAsync();
        var schemas = await conn.ListSchemasAsync(CancellationToken.None);
        schemas.Should().Contain(s => s.Name == "public");
    }

    [SkippableFact]
    public async Task Describe_table_returns_columns_and_pk()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteNonQueryAsync(new NonQueryRequest(
            "DROP TABLE IF EXISTS mdqa_users; CREATE TABLE mdqa_users (id serial PRIMARY KEY, email text NOT NULL);",
            null, null), CancellationToken.None);

        var details = await conn.DescribeTableAsync("public", "mdqa_users", CancellationToken.None);
        details.Columns.Should().HaveCount(2);
        details.Columns[0].IsPrimaryKey.Should().BeTrue();
        details.Columns[1].IsNullable.Should().BeFalse();
    }

    [SkippableFact]
    public async Task List_tables_paginates()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteNonQueryAsync(new NonQueryRequest("DROP SCHEMA IF EXISTS mdqa_page CASCADE; CREATE SCHEMA mdqa_page;", null, null), CancellationToken.None);
        for (var i = 0; i < 5; i++)
        {
            await conn.ExecuteNonQueryAsync(new NonQueryRequest($"CREATE TABLE mdqa_page.t{i}(id int);", null, null), CancellationToken.None);
        }

        var (items, total) = await conn.ListTablesAsync("mdqa_page", new PageRequest(0, 2), CancellationToken.None);
        items.Should().HaveCount(2);
        total.Should().Be(5);
    }

    [SkippableFact]
    public async Task Explain_returns_json_plan()
    {
        await using var conn = await OpenAsync();
        var plan = await conn.ExplainAsync("SELECT 1", null, CancellationToken.None);
        plan.Format.Should().Be("json");
        plan.Plan.Should().NotBeNullOrEmpty();
        plan.Plan.Should().StartWith("[");
    }

    [SkippableFact]
    public async Task List_roles_includes_postgres()
    {
        await using var conn = await OpenAsync();
        var roles = await conn.ListRolesAsync(CancellationToken.None);
        roles.Should().Contain(r => r.Name == "postgres");
    }

    [SkippableFact]
    public async Task List_databases_includes_test_db()
    {
        await using var conn = await OpenAsync();
        var dbs = await conn.ListDatabasesAsync(CancellationToken.None);
        dbs.Should().Contain(d => d.Name == _fixture.Database);
    }

    [SkippableFact]
    public async Task Connection_string_never_leaks_password_when_redacted()
    {
        var provider = new PostgresProvider(NullLoggerFactory.Instance);
        var descriptor = new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = "pg-test",
            Provider = DatabaseKind.Postgres,
            Host = "example.com",
            Database = "app",
            Username = "u",
            SslMode = "Require",
        };
        var connString = provider.BuildConnectionString(descriptor, "supersecret");
        connString.Should().Contain("supersecret");
        McpDatabaseQueryApp.Core.Security.ConnectionStringRedactor.Redact(connString).Should().NotContain("supersecret");
    }
}
