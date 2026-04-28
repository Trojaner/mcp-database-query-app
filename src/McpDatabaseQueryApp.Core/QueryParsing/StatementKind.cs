namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Coarse classification of a single SQL statement. New kinds may be added
/// over time; consumers must treat unknown values as <see cref="Other"/>.
/// </summary>
public enum StatementKind
{
    Unknown = 0,
    Select,
    Insert,
    Update,
    Delete,
    Merge,
    Truncate,
    CreateTable,
    AlterTable,
    DropTable,
    CreateView,
    AlterView,
    DropView,
    CreateIndex,
    DropIndex,
    CreateSchema,
    DropSchema,
    CreateProcedure,
    AlterProcedure,
    DropProcedure,
    CreateFunction,
    AlterFunction,
    DropFunction,
    Grant,
    Revoke,
    Execute,
    Transaction,
    Set,
    Use,
    Other,
}
