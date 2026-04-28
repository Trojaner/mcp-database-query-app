namespace McpDatabaseQueryApp.Core.QueryParsing;

public enum ColumnUsage
{
    Projected,
    Filtered,
    Modified,
    Inserted,
    Joined,
    OrderedBy,
    GroupedBy,
}
