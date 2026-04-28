using McpDatabaseQueryApp.Core.QueryParsing;
using PgSqlParser;

namespace McpDatabaseQueryApp.Providers.Postgres.QueryParsing;

/// <summary>
/// Pure function: maps a libpg_query <see cref="Node"/> (top-level
/// statement) to the high-level Core classification. Kept separate from
/// the action extractor so unit tests can target it in isolation.
/// </summary>
internal static class PgStatementClassifier
{
    /// <summary>
    /// Classifies <paramref name="statement"/>. Returns
    /// <c>(Other, false, false)</c> for nodes the parser cannot
    /// categorise.
    /// </summary>
    public static (StatementKind Kind, bool IsMutation, bool IsDestructive) Classify(Node statement)
    {
        switch (statement.NodeCase)
        {
            case Node.NodeOneofCase.SelectStmt:
                return (StatementKind.Select, false, false);

            case Node.NodeOneofCase.InsertStmt:
                return (StatementKind.Insert, true, false);

            case Node.NodeOneofCase.UpdateStmt:
                {
                    var upd = statement.UpdateStmt;
                    return (StatementKind.Update, true, upd.WhereClause is null);
                }

            case Node.NodeOneofCase.DeleteStmt:
                {
                    var del = statement.DeleteStmt;
                    return (StatementKind.Delete, true, del.WhereClause is null);
                }

            case Node.NodeOneofCase.MergeStmt:
                return (StatementKind.Merge, true, false);

            case Node.NodeOneofCase.TruncateStmt:
                return (StatementKind.Truncate, true, true);

            case Node.NodeOneofCase.CreateStmt:
                return (StatementKind.CreateTable, true, false);

            case Node.NodeOneofCase.AlterTableStmt:
                {
                    var alter = statement.AlterTableStmt;
                    bool destructive = false;
                    foreach (var cmd in alter.Cmds)
                    {
                        if (cmd.NodeCase == Node.NodeOneofCase.AlterTableCmd)
                        {
                            var sub = cmd.AlterTableCmd.Subtype;
                            if (sub == AlterTableType.AtDropColumn
                                || sub == AlterTableType.AtDropConstraint)
                            {
                                destructive = true;
                                break;
                            }
                        }
                    }
                    return (StatementKind.AlterTable, true, destructive);
                }

            case Node.NodeOneofCase.DropStmt:
                return ClassifyDrop(statement.DropStmt);

            case Node.NodeOneofCase.ViewStmt:
                return (StatementKind.CreateView, true, false);

            case Node.NodeOneofCase.IndexStmt:
                return (StatementKind.CreateIndex, true, false);

            case Node.NodeOneofCase.CreateSchemaStmt:
                return (StatementKind.CreateSchema, true, false);

            case Node.NodeOneofCase.CreateFunctionStmt:
                return (StatementKind.CreateFunction, true, false);

            case Node.NodeOneofCase.AlterFunctionStmt:
                return (StatementKind.AlterFunction, true, false);

            case Node.NodeOneofCase.GrantStmt:
                {
                    var g = statement.GrantStmt;
                    return (g.IsGrant ? StatementKind.Grant : StatementKind.Revoke, true, false);
                }

            case Node.NodeOneofCase.CallStmt:
            case Node.NodeOneofCase.ExecuteStmt:
                return (StatementKind.Execute, false, false);

            case Node.NodeOneofCase.TransactionStmt:
                return (StatementKind.Transaction, false, false);

            case Node.NodeOneofCase.VariableSetStmt:
            case Node.NodeOneofCase.VariableShowStmt:
                return (StatementKind.Set, false, false);

            case Node.NodeOneofCase.CreatedbStmt:
            case Node.NodeOneofCase.AlterDatabaseStmt:
                return (StatementKind.Other, true, false);

            case Node.NodeOneofCase.DropdbStmt:
                return (StatementKind.Other, true, true);

            default:
                return (StatementKind.Other, false, false);
        }
    }

    private static (StatementKind Kind, bool IsMutation, bool IsDestructive) ClassifyDrop(DropStmt drop)
    {
        return drop.RemoveType switch
        {
            ObjectType.ObjectTable => (StatementKind.DropTable, true, true),
            ObjectType.ObjectView or ObjectType.ObjectMatview
                => (StatementKind.DropView, true, true),
            ObjectType.ObjectIndex => (StatementKind.DropIndex, true, true),
            ObjectType.ObjectSchema => (StatementKind.DropSchema, true, true),
            ObjectType.ObjectFunction or ObjectType.ObjectRoutine
                => (StatementKind.DropFunction, true, true),
            ObjectType.ObjectProcedure => (StatementKind.DropProcedure, true, true),
            _ => (StatementKind.Other, true, true),
        };
    }
}
