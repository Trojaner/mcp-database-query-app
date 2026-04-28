namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// One discrete action a statement performs against a single object.
/// </summary>
public sealed record ParsedQueryAction(
    ActionKind Action,
    ObjectReference Target,
    IReadOnlyList<ColumnReference> Columns)
{
    public static ParsedQueryAction Of(ActionKind action, ObjectReference target)
        => new(action, target, []);
}
