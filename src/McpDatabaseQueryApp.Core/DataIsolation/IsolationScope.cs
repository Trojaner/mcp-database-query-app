namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Pinpoints the <c>(host, port, database, schema, table)</c> tuple a rule
/// applies to. All five fields are required: rules are intentionally
/// table-specific. Any inbound query whose connection descriptor matches the
/// host/port/database AND whose parsed AST touches the schema/table triggers
/// the rule, regardless of which connection id was used.
/// </summary>
/// <param name="Host">Database host (case-insensitive match).</param>
/// <param name="Port">TCP port.</param>
/// <param name="DatabaseName">Database/catalog name (case-insensitive match).</param>
/// <param name="Schema">Schema name (case-insensitive match).</param>
/// <param name="Table">Table name (case-insensitive match).</param>
public sealed record IsolationScope(
    string Host,
    int Port,
    string DatabaseName,
    string Schema,
    string Table)
{
    /// <summary>
    /// Returns true when <paramref name="descriptor"/>'s host/port/database
    /// match this scope. Comparison is case-insensitive on identifiers.
    /// </summary>
    public bool MatchesConnection(Connections.ConnectionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        if (!string.Equals(descriptor.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (descriptor.Port is null || descriptor.Port.Value != Port)
        {
            return false;
        }
        if (!string.Equals(descriptor.Database, DatabaseName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true when <paramref name="reference"/> targets this scope's
    /// schema and table. Comparison is case-insensitive.
    /// </summary>
    public bool MatchesObject(QueryParsing.ObjectReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        if (!string.Equals(reference.Name, Table, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        // A null schema on the reference matches any schema (parser produced
        // an unqualified table name) — fall back to the rule's schema.
        if (reference.Schema is not null
            && !string.Equals(reference.Schema, Schema, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return true;
    }
}
