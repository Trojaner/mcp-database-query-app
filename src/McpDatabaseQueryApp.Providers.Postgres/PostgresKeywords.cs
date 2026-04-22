namespace McpDatabaseQueryApp.Providers.Postgres;

internal static class PostgresKeywords
{
    public static readonly IReadOnlyList<string> All =
    [
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "RETURNING",
        "CREATE TABLE", "CREATE SCHEMA", "CREATE INDEX", "CREATE VIEW", "CREATE MATERIALIZED VIEW",
        "ALTER TABLE", "DROP TABLE", "TRUNCATE", "COPY", "EXPLAIN", "ANALYZE", "VACUUM",
        "WITH", "UNION", "INTERSECT", "EXCEPT",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL JOIN", "CROSS JOIN",
        "ON CONFLICT", "DO UPDATE", "DO NOTHING",
        "BEGIN", "COMMIT", "ROLLBACK", "SAVEPOINT",
        "LISTEN", "NOTIFY", "UNLISTEN",
    ];
}
