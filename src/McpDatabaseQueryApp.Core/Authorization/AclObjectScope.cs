namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Tuple of optional, hierarchical fields that select which database objects
/// an <see cref="AclEntry"/> applies to. A <c>null</c> field is a wildcard:
/// it matches any value. A non-null field matches only the literal value
/// (case-insensitive ordinal equality). Equality on the record itself is
/// purely structural so two scopes carrying the same hostnames/etc. are
/// considered identical.
/// </summary>
/// <param name="Host">Database server host (or <c>null</c> wildcard).</param>
/// <param name="Port">Database server port (or <c>null</c> wildcard).</param>
/// <param name="DatabaseName">Database/catalog name (or <c>null</c> wildcard).</param>
/// <param name="Schema">Schema name (or <c>null</c> wildcard).</param>
/// <param name="Table">Table or view name (or <c>null</c> wildcard).</param>
/// <param name="Column">Column name (or <c>null</c> wildcard, including "any column").</param>
public sealed record AclObjectScope(
    string? Host,
    int? Port,
    string? DatabaseName,
    string? Schema,
    string? Table,
    string? Column)
{
    /// <summary>
    /// The fully-wildcard scope (every field <c>null</c>). Matches every
    /// object on every connection.
    /// </summary>
    public static AclObjectScope Any { get; } = new(
        Host: null,
        Port: null,
        DatabaseName: null,
        Schema: null,
        Table: null,
        Column: null);

    /// <summary>
    /// Returns <c>true</c> when this scope targets a specific column rather
    /// than an entire table.
    /// </summary>
    public bool IsColumnScoped => Column is not null;
}
