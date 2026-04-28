using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;

/// <summary>
/// Pure function: maps a ScriptDom statement node to a high-level
/// classification (kind / mutation / destructive). Kept separate from the
/// extraction visitor so unit tests can target it directly.
/// </summary>
internal static class TSqlStatementClassifier
{
    public static (StatementKind Kind, bool IsMutation, bool IsDestructive) Classify(TSqlStatement statement)
    {
        switch (statement)
        {
            case SelectStatement:
                return (StatementKind.Select, false, false);

            case InsertStatement:
                return (StatementKind.Insert, true, false);

            case UpdateStatement upd:
                return (StatementKind.Update, true, IsUnboundedUpdate(upd));

            case DeleteStatement del:
                return (StatementKind.Delete, true, IsUnboundedDelete(del));

            case MergeStatement:
                return (StatementKind.Merge, true, false);

            case TruncateTableStatement:
                return (StatementKind.Truncate, true, true);

            case CreateTableStatement:
                return (StatementKind.CreateTable, true, false);
            case AlterTableAddTableElementStatement:
            case AlterTableAlterColumnStatement:
            case AlterTableConstraintModificationStatement:
            case AlterTableDropTableElementStatement:
            case AlterTableSetStatement:
            case AlterTableSwitchStatement:
            case AlterTableTriggerModificationStatement:
                return (StatementKind.AlterTable, true, statement is AlterTableDropTableElementStatement);
            case DropTableStatement:
                return (StatementKind.DropTable, true, true);

            case CreateViewStatement:
                return (StatementKind.CreateView, true, false);
            case AlterViewStatement:
                return (StatementKind.AlterView, true, false);
            case DropViewStatement:
                return (StatementKind.DropView, true, true);

            case CreateIndexStatement:
                return (StatementKind.CreateIndex, true, false);
            case DropIndexStatement:
                return (StatementKind.DropIndex, true, true);

            case CreateSchemaStatement:
                return (StatementKind.CreateSchema, true, false);
            case DropSchemaStatement:
                return (StatementKind.DropSchema, true, true);

            case CreateProcedureStatement:
                return (StatementKind.CreateProcedure, true, false);
            case AlterProcedureStatement:
                return (StatementKind.AlterProcedure, true, false);
            case DropProcedureStatement:
                return (StatementKind.DropProcedure, true, true);

            case CreateFunctionStatement:
                return (StatementKind.CreateFunction, true, false);
            case AlterFunctionStatement:
                return (StatementKind.AlterFunction, true, false);
            case DropFunctionStatement:
                return (StatementKind.DropFunction, true, true);

            case GrantStatement:
                return (StatementKind.Grant, true, false);
            case RevokeStatement:
                return (StatementKind.Revoke, true, false);

            case ExecuteStatement:
                return (StatementKind.Execute, false, false);

            case BeginTransactionStatement:
            case CommitTransactionStatement:
            case RollbackTransactionStatement:
            case SaveTransactionStatement:
                return (StatementKind.Transaction, false, false);

            case PredicateSetStatement:
            case SetVariableStatement:
                return (StatementKind.Set, false, false);

            case UseStatement:
                return (StatementKind.Use, false, false);

            default:
                return (StatementKind.Other, false, false);
        }
    }

    private static bool IsUnboundedUpdate(UpdateStatement statement)
        => statement.UpdateSpecification?.WhereClause is null;

    private static bool IsUnboundedDelete(DeleteStatement statement)
        => statement.DeleteSpecification?.WhereClause is null;
}
