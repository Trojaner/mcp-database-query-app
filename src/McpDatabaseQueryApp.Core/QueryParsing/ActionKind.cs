namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Type of access a statement performs against an <see cref="ObjectReference"/>.
/// A single statement may produce multiple actions (e.g. an INSERT … SELECT
/// produces both <see cref="Insert"/> and <see cref="Read"/>).
/// </summary>
public enum ActionKind
{
    Read,
    Insert,
    Update,
    Delete,
    Truncate,
    Create,
    Alter,
    Drop,
    Grant,
    Revoke,
    Execute,
}
