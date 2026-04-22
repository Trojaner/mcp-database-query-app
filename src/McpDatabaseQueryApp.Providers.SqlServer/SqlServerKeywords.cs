namespace McpDatabaseQueryApp.Providers.SqlServer;

internal static class SqlServerKeywords
{
    public static readonly IReadOnlyList<string> All =
    [
        "SELECT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "TOP", "OFFSET", "FETCH NEXT",
        "INSERT INTO", "VALUES", "UPDATE", "SET", "DELETE FROM", "OUTPUT", "MERGE",
        "CREATE TABLE", "CREATE INDEX", "CREATE VIEW", "CREATE PROCEDURE", "CREATE SCHEMA",
        "ALTER TABLE", "DROP TABLE", "TRUNCATE TABLE", "EXEC", "EXECUTE",
        "WITH", "UNION", "INTERSECT", "EXCEPT",
        "INNER JOIN", "LEFT JOIN", "RIGHT JOIN", "FULL OUTER JOIN", "CROSS APPLY", "OUTER APPLY",
        "BEGIN TRAN", "COMMIT TRAN", "ROLLBACK TRAN", "SAVE TRAN",
        "SP_WHO", "SP_WHO2", "SP_HELP",
    ];
}
