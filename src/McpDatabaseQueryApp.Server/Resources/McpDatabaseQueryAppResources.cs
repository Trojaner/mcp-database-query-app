using System.ComponentModel;
using System.Text.Json;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Core.Storage;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Resources;

[McpServerResourceType]
public sealed class McpDatabaseQueryAppResources
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    private readonly IProviderRegistry _providers;
    private readonly IConnectionRegistry _registry;
    private readonly IMetadataStore _metadata;
    private readonly IScriptStore _scripts;

    public McpDatabaseQueryAppResources(
        IProviderRegistry providers,
        IConnectionRegistry registry,
        IMetadataStore metadata,
        IScriptStore scripts)
    {
        _providers = providers;
        _registry = registry;
        _metadata = metadata;
        _scripts = scripts;
    }

    [McpServerResource(UriTemplate = "mcpdb://providers", Name = "providers", Title = "Database providers", MimeType = "application/json")]
    [Description("Lists supported database providers and their capabilities.")]
    public string Providers()
    {
        var items = _providers.All.Select(p => new
        {
            kind = p.Kind.ToString(),
            capabilities = p.Capabilities,
        }).ToArray();
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://databases", Name = "databases", Title = "Configured databases", MimeType = "application/json")]
    [Description("Lists all pre-defined databases. Credentials are redacted.")]
    public async Task<string> DatabasesAsync(CancellationToken cancellationToken)
    {
        var (items, total) = await _metadata.ListDatabasesAsync(0, 500, null, cancellationToken).ConfigureAwait(false);
        var active = _registry.List().Select(c => c.Descriptor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var payload = new
        {
            total,
            items = items.Select(d => new
            {
                descriptor = RedactedDescriptor.From(d),
                active = active.Contains(d.Name),
            }).ToArray(),
        };
        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://databases/{name}", Name = "database", MimeType = "application/json")]
    [Description("Redacted descriptor for a specific pre-defined database.")]
    public async Task<string> DatabaseAsync(string name, CancellationToken cancellationToken)
    {
        var record = await _metadata.GetDatabaseAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Database '{name}' not found.");
        return JsonSerializer.Serialize(RedactedDescriptor.From(record.Descriptor), JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://connections", Name = "connections", Title = "Active connections", MimeType = "application/json")]
    [Description("Currently open connections, with redacted descriptors.")]
    public string ActiveConnections()
    {
        var items = _registry.List().Select(conn => new
        {
            id = conn.Id,
            descriptor = RedactedDescriptor.From(conn.Descriptor),
        }).ToArray();
        return JsonSerializer.Serialize(items, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://connections/{id}/schemas", Name = "connection_schemas", MimeType = "application/json")]
    [Description("Schemas available on an active connection.")]
    public async Task<string> SchemasAsync(string id, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(id, out var connection))
        {
            throw new KeyNotFoundException($"Connection '{id}' not found.");
        }

        var schemas = await connection.ListSchemasAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(schemas, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://connections/{id}/schemas/{schema}/tables", Name = "connection_tables", MimeType = "application/json")]
    [Description("Tables in a schema on an active connection.")]
    public async Task<string> TablesAsync(string id, string schema, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(id, out var connection))
        {
            throw new KeyNotFoundException($"Connection '{id}' not found.");
        }

        var (items, total) = await connection.ListTablesAsync(schema, new PageRequest(0, 500), cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { total, items }, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://connections/{id}/schemas/{schema}/tables/{table}", Name = "connection_table_details", MimeType = "application/json")]
    [Description("Columns, indexes, and foreign keys for a single table.")]
    public async Task<string> TableDetailsAsync(string id, string schema, string table, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(id, out var connection))
        {
            throw new KeyNotFoundException($"Connection '{id}' not found.");
        }

        var details = await connection.DescribeTableAsync(schema, table, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(details, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://connections/{id}/roles", Name = "connection_roles", MimeType = "application/json")]
    [Description("Database roles/logins visible from the connection.")]
    public async Task<string> RolesAsync(string id, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet(id, out var connection))
        {
            throw new KeyNotFoundException($"Connection '{id}' not found.");
        }

        var roles = await connection.ListRolesAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(roles, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://scripts", Name = "scripts", Title = "Saved SQL scripts", MimeType = "application/json")]
    [Description("All saved SQL scripts.")]
    public async Task<string> ScriptsAsync(CancellationToken cancellationToken)
    {
        var (items, total) = await _scripts.ListAsync(0, 500, null, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { total, items }, JsonOptions);
    }

    [McpServerResource(UriTemplate = "mcpdb://scripts/{name}", Name = "script", MimeType = "application/json")]
    [Description("A single saved SQL script by name or id.")]
    public async Task<string> ScriptAsync(string name, CancellationToken cancellationToken)
    {
        var record = await _scripts.GetAsync(name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Script '{name}' not found.");
        return JsonSerializer.Serialize(record, JsonOptions);
    }
}
