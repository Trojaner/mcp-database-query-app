namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// A driver-neutral reference to a database object. Up to four parts are
/// captured (server, database/catalog, schema, name). Providers normalize
/// quoted identifiers before populating; case is preserved verbatim.
/// </summary>
public sealed record ObjectReference(
    ObjectKind Kind,
    string? Server,
    string? Database,
    string? Schema,
    string Name)
{
    /// <summary>
    /// Convenience helper for the common 2-part schema+name case.
    /// </summary>
    public static ObjectReference Schemaed(ObjectKind kind, string? schema, string name)
        => new(kind, Server: null, Database: null, schema, name);

    /// <summary>
    /// Returns the most-qualified identifier suitable for human display.
    /// Each non-null part is joined with '.'.
    /// </summary>
    public string QualifiedName
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(Server))
            {
                parts.Add(Server);
            }
            if (!string.IsNullOrEmpty(Database))
            {
                parts.Add(Database);
            }
            if (!string.IsNullOrEmpty(Schema))
            {
                parts.Add(Schema);
            }
            parts.Add(Name);
            return string.Join('.', parts);
        }
    }
}
