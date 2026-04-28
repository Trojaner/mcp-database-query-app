using McpDatabaseQueryApp.Core.QueryParsing;
using PgSqlParser;

namespace McpDatabaseQueryApp.Providers.Postgres.QueryParsing;

internal static class PgNameExtensions
{
    public static ObjectReference ToObjectReference(this RangeVar? rv, ObjectKind kind)
    {
        if (rv is null)
        {
            return new ObjectReference(kind, null, null, null, string.Empty);
        }
        string? catalog = string.IsNullOrEmpty(rv.Catalogname) ? null : rv.Catalogname;
        string? schema = string.IsNullOrEmpty(rv.Schemaname) ? null : rv.Schemaname;
        return new ObjectReference(kind, null, catalog, schema, rv.Relname ?? string.Empty);
    }

    /// <summary>
    /// Converts a libpg_query "list of String nodes" naming a database
    /// object into an <see cref="ObjectReference"/>. libpg_query encodes
    /// objects in <see cref="DropStmt"/> / <see cref="GrantStmt"/> as
    /// nested <see cref="List"/>s of <c>String</c> nodes whose values are
    /// the dotted parts.
    /// </summary>
    public static ObjectReference ListToObjectReference(IEnumerable<Node> identifierList, ObjectKind kind)
    {
        var parts = new List<string>();
        foreach (var n in identifierList)
        {
            if (n.NodeCase == Node.NodeOneofCase.String)
            {
                parts.Add(n.String.Sval ?? string.Empty);
            }
            else if (n.NodeCase == Node.NodeOneofCase.List)
            {
                foreach (var inner in n.List.Items)
                {
                    if (inner.NodeCase == Node.NodeOneofCase.String)
                    {
                        parts.Add(inner.String.Sval ?? string.Empty);
                    }
                }
            }
        }
        return PartsToObjectReference(parts, kind);
    }

    public static ObjectReference PartsToObjectReference(IReadOnlyList<string> parts, ObjectKind kind)
    {
        return parts.Count switch
        {
            0 => new ObjectReference(kind, null, null, null, string.Empty),
            1 => new ObjectReference(kind, null, null, null, parts[0]),
            2 => new ObjectReference(kind, null, null, parts[0], parts[1]),
            3 => new ObjectReference(kind, null, parts[0], parts[1], parts[2]),
            _ => new ObjectReference(kind, null, parts[^3], parts[^2], parts[^1]),
        };
    }

    public static (string? Schema, string? Table, string Column)? ToColumnQualifiers(this ColumnRef cr)
    {
        if (cr is null || cr.Fields.Count == 0)
        {
            return null;
        }
        var parts = new List<string>(cr.Fields.Count);
        foreach (var node in cr.Fields)
        {
            if (node.NodeCase == Node.NodeOneofCase.String)
            {
                parts.Add(node.String.Sval ?? string.Empty);
            }
            else if (node.NodeCase == Node.NodeOneofCase.AStar)
            {
                parts.Add("*");
            }
            else
            {
                return null;
            }
        }
        return parts.Count switch
        {
            1 => (null, null, parts[0]),
            2 => (null, parts[0], parts[1]),
            _ => (parts[^3], parts[^2], parts[^1]),
        };
    }
}
