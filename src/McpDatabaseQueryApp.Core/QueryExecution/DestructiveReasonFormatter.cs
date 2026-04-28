using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.QueryExecution;

/// <summary>
/// Builds the human-readable reason string for a destructive statement. The
/// format is intentionally short and stable so confirmation prompts and tool
/// error messages stay aligned.
/// </summary>
internal static class DestructiveReasonFormatter
{
    public static string Format(ParsedStatement statement)
    {
        ArgumentNullException.ThrowIfNull(statement);

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
}
