using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;

internal static class TSqlNameExtensions
{
    /// <summary>
    /// Maps a ScriptDom <see cref="SchemaObjectName"/> (1-to-4 part name)
    /// to a Core <see cref="ObjectReference"/> with the supplied
    /// <see cref="ObjectKind"/>.
    /// </summary>
    public static ObjectReference ToObjectReference(this SchemaObjectName? name, ObjectKind kind)
    {
        if (name is null)
        {
            return new ObjectReference(kind, null, null, null, string.Empty);
        }

        // ScriptDom orders identifiers from least to most specific. With
        // four parts: Server.Database.Schema.Name. Missing parts are null.
        string? server = name.ServerIdentifier?.Value;
        string? database = name.DatabaseIdentifier?.Value;
        string? schema = name.SchemaIdentifier?.Value;
        string objectName = name.BaseIdentifier?.Value ?? string.Empty;

        return new ObjectReference(kind, server, database, schema, objectName);
    }

    public static string? ColumnName(this ColumnReferenceExpression expr)
    {
        var ids = expr.MultiPartIdentifier?.Identifiers;
        if (ids is null || ids.Count == 0)
        {
            return null;
        }
        return ids[^1].Value;
    }

    public static (string? Schema, string? Table) ColumnQualifiers(this ColumnReferenceExpression expr)
    {
        var ids = expr.MultiPartIdentifier?.Identifiers;
        if (ids is null || ids.Count <= 1)
        {
            return (null, null);
        }
        // Last is column. Second-to-last is table. Third-to-last is schema.
        var table = ids[^2].Value;
        string? schema = ids.Count >= 3 ? ids[^3].Value : null;
        return (schema, table);
    }
}
