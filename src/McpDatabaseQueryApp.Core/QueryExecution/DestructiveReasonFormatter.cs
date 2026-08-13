using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Builds the human-readable reason string for a state-changing statement.
/// The format is intentionally short and stable so confirmation prompts and
/// tool error messages stay aligned.
/// </summary>
internal static class DestructiveReasonFormatter
{
    public static string Format(ParsedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

        return statement.IsDestructive
            ? FormatDestructive(statement)
            : FormatWrite(statement);
    }

    private static string FormatDestructive(ParsedStatement statement)
    {
        return statement.StatementKind switch
        {
            StatementKind.Delete => "DELETE without a WHERE clause removes every row.",
            StatementKind.Update => "UPDATE without a WHERE clause rewrites every row.",
            StatementKind.Truncate => "TRUNCATE empties the target table irreversibly.",
            StatementKind.DropTable => "DROP TABLE permanently removes the table and its data.",
            StatementKind.DropView => "DROP VIEW removes the view definition.",
            StatementKind.DropIndex => "DROP INDEX removes the index.",
            StatementKind.DropSchema => "DROP SCHEMA removes the schema and any objects within it.",
            StatementKind.DropProcedure => "DROP PROCEDURE removes the stored procedure.",
            StatementKind.DropFunction => "DROP FUNCTION removes the function.",
            StatementKind.AlterTable => "ALTER TABLE modifies the table definition.",
            StatementKind.Grant => "GRANT changes access privileges.",
            StatementKind.Revoke => "REVOKE changes access privileges.",
            _ => $"{statement.StatementKind} is a destructive operation.",
        };
    }

    private static string FormatWrite(ParsedStatement statement)
    {
        return statement.StatementKind switch
        {
            StatementKind.Insert => "INSERT adds rows to the target table.",
            StatementKind.Update => "UPDATE rewrites the rows matched by its WHERE clause.",
            StatementKind.Delete => "DELETE removes the rows matched by its WHERE clause.",
            StatementKind.Merge => "MERGE inserts, updates or deletes rows in the target table.",
            StatementKind.CreateTable => "CREATE TABLE adds a table to the schema.",
            StatementKind.CreateView => "CREATE VIEW adds a view to the schema.",
            StatementKind.AlterView => "ALTER VIEW changes the view definition.",
            StatementKind.CreateIndex => "CREATE INDEX adds an index to the target table.",
            StatementKind.CreateSchema => "CREATE SCHEMA adds a schema to the database.",
            StatementKind.CreateProcedure => "CREATE PROCEDURE adds a stored procedure.",
            StatementKind.AlterProcedure => "ALTER PROCEDURE changes the stored procedure definition.",
            StatementKind.CreateFunction => "CREATE FUNCTION adds a function.",
            StatementKind.AlterFunction => "ALTER FUNCTION changes the function definition.",
            StatementKind.Execute => "EXECUTE runs a routine that may change data.",
            _ => $"{statement.StatementKind} changes persistent state.",
        };
    }
}
