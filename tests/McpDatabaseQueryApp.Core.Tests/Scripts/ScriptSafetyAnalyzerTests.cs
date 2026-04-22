using McpDatabaseQueryApp.Core.Scripts;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Scripts;

public sealed class ScriptSafetyAnalyzerTests
{
    [Theory]
    [InlineData("DROP TABLE users;")]
    [InlineData("drop table users;")]
    [InlineData("TRUNCATE users;")]
    [InlineData("GRANT ALL ON users TO bob;")]
    [InlineData("REVOKE SELECT ON users FROM alice;")]
    [InlineData("ALTER TABLE users DROP COLUMN email;")]
    [InlineData("DELETE FROM users;")]
    [InlineData("UPDATE users SET active = false;")]
    [InlineData("SELECT 1; DROP TABLE users;")]
    public void Flags_destructive_sql(string sql)
    {
        ScriptSafetyAnalyzer.IsLikelyDestructive(sql).Should().BeTrue();
    }

    [Theory]
    [InlineData("SELECT * FROM users WHERE id = 1;")]
    [InlineData("SELECT COUNT(*) FROM users;")]
    [InlineData("UPDATE users SET name = 'x' WHERE id = 1;")]
    [InlineData("DELETE FROM users WHERE id = 1;")]
    [InlineData("INSERT INTO logs(msg) VALUES ('ok');")]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x;")]
    [InlineData("")]
    public void Does_not_flag_safe_sql(string sql)
    {
        ScriptSafetyAnalyzer.IsLikelyDestructive(sql).Should().BeFalse();
    }

    [Theory]
    [InlineData("SELECT 1;")]
    [InlineData("WITH cte AS (SELECT 1) SELECT * FROM cte;")]
    [InlineData("EXPLAIN SELECT * FROM users;")]
    [InlineData("SHOW TABLES;")]
    public void Identifies_readonly_sql(string sql)
    {
        ScriptSafetyAnalyzer.IsReadOnly(sql).Should().BeTrue();
    }

    [Theory]
    [InlineData("INSERT INTO logs VALUES (1);")]
    [InlineData("UPDATE users SET name = 'x';")]
    [InlineData("DELETE FROM users;")]
    public void Identifies_non_readonly_sql(string sql)
    {
        ScriptSafetyAnalyzer.IsReadOnly(sql).Should().BeFalse();
    }

    [Fact]
    public void Ignores_commented_out_destructive_keywords()
    {
        const string sql = """
            -- DROP TABLE users;
            /* TRUNCATE audit; */
            SELECT 1;
            """;
        ScriptSafetyAnalyzer.IsLikelyDestructive(sql).Should().BeFalse();
    }
}
