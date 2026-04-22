using System.ComponentModel;
using System.Diagnostics;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Server.Metadata;
using McpDatabaseQueryApp.Server.Pagination;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class SchemaTools
{
    private readonly IConnectionRegistry _registry;
    private readonly MetadataCache _cache;
    private readonly INoteStore _notes;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly ILogger<SchemaTools> _logger;

    public SchemaTools(
        IConnectionRegistry registry,
        MetadataCache cache,
        INoteStore notes,
        McpDatabaseQueryAppOptions options,
        ILogger<SchemaTools> logger)
    {
        _registry = registry;
        _cache = cache;
        _notes = notes;
        _options = options;
        _logger = logger;
    }

    [McpServerTool(Name = "db_schemas_list", ReadOnly = true)]
    [Description("Lists schemas/namespaces available on the connection.")]
    public async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(
        string connectionId,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var schemas = await connection.ListSchemasAsync(cancellationToken).ConfigureAwait(false);
        var meta = _cache.GetOrCreate(connectionId);
        meta.Schemas = schemas;
        return schemas;
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_tables_list", ReadOnly = true)]
    [Description("Lists tables/views on the connection. Supports schema filtering and cursor-based pagination.")]
    public async Task<TablePageResult> ListTablesAsync(
        string connectionId,
        [Description("Schema to filter to. Null means all schemas.")] string? schema,
        [Description("Optional SQL LIKE substring filter for the table name.")] string? filter,
        [Description("Pagination cursor.")] string? cursor,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var page = PageCodec.Decode(cursor, defaultLimit: 100);
        var request = new PageRequest(page.Offset, page.Limit, filter);
        var (items, total) = await connection.ListTablesAsync(schema, request, cancellationToken).ConfigureAwait(false);
        var next = PageCodec.EncodeNext(page, items.Count, total);

        if (!string.IsNullOrEmpty(schema))
        {
            var meta = _cache.GetOrCreate(connectionId);
            var existing = new Dictionary<string, IReadOnlyList<TableInfo>>(meta.Tables, StringComparer.OrdinalIgnoreCase)
            {
                [schema] = items,
            };
            meta.Tables = existing;
        }

        return new TablePageResult(connectionId, schema, items, total, next);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_table_describe", ReadOnly = true)]
    [Description("Returns columns, indexes, foreign keys, and any attached notes for a table.")]
    public async Task<TableDescribeResult> DescribeAsync(
        string connectionId,
        string schema,
        string table,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
        var details = await connection.DescribeTableAsync(schema, table, cancellationToken).ConfigureAwait(false);

        var tableNote = await _notes.GetAsync(NoteTargetType.Table, $"{schema}.{table}", cancellationToken).ConfigureAwait(false);
        return new TableDescribeResult(details, tableNote?.NoteText);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_describe_batch", ReadOnly = true)]
    [Description("Describes multiple tables in one call. Targets: '*' for all, 'schema.*' for all in a schema, 'schema.table' for a specific table.")]
    public async Task<BatchDescribeResult> DescribeBatchAsync(
        string connectionId,
        [Description("Target expressions: '*', 'schema.*', or 'schema.table'.")] IReadOnlyList<string> targets,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var sw = Stopwatch.StartNew();
        var connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);

        var resolved = new List<(string Schema, string Table)>();
        foreach (var target in targets)
        {
            if (target == "*")
            {
                var schemas = await connection.ListSchemasAsync(cancellationToken).ConfigureAwait(false);
                foreach (var s in schemas)
                {
                    var (schemaTables, _) = await connection.ListTablesAsync(s.Name, new PageRequest(0, 10000, null), cancellationToken).ConfigureAwait(false);
                    resolved.AddRange(schemaTables.Select(t => (s.Name, t.Name)));
                }
            }
            else if (target.EndsWith(".*", StringComparison.Ordinal))
            {
                var schemaName = target[..^2];
                var (filteredTables, _) = await connection.ListTablesAsync(schemaName, new PageRequest(0, 10000, null), cancellationToken).ConfigureAwait(false);
                resolved.AddRange(filteredTables.Select(t => (schemaName, t.Name)));
            }
            else
            {
                var parts = target.Split('.', 2);
                if (parts.Length == 2)
                {
                    resolved.Add((parts[0], parts[1]));
                }
            }
        }

        var tasks = resolved.Select(async r =>
        {
            var details = await connection.DescribeTableAsync(r.Schema, r.Table, cancellationToken).ConfigureAwait(false);
            var note = await _notes.GetAsync(NoteTargetType.Table, $"{r.Schema}.{r.Table}", cancellationToken).ConfigureAwait(false);
            return new TableDescribeResult(details, note?.NoteText);
        });

        var tables = await Task.WhenAll(tasks).ConfigureAwait(false);
        sw.Stop();
        return new BatchDescribeResult(connectionId, tables.ToList(), tables.Length, sw.ElapsedMilliseconds);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_roles_list", ReadOnly = true)]
    [Description("Lists database roles/logins. Credentials are never returned.")]
    public async Task<IReadOnlyList<RoleInfo>> ListRolesAsync(string connectionId, CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
        return await connection.ListRolesAsync(cancellationToken).ConfigureAwait(false);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_databases_list", ReadOnly = true)]
    [Description("Lists databases/catalogs visible from the connection.")]
    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(string connectionId, CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var connection = await ResolveConnectionAsync(connectionId, cancellationToken).ConfigureAwait(false);
        return await connection.ListDatabasesAsync(cancellationToken).ConfigureAwait(false);
        }, _logger).ConfigureAwait(false);
    }

    private async Task<IDatabaseConnection> ResolveConnectionAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (_registry.TryGet(connectionId, out var connection))
        {
            return connection;
        }

        if (_options.AutoConnect)
        {
            return await _registry.GetOrOpenPredefinedAsync(connectionId, cancellationToken).ConfigureAwait(false);
        }

        throw new KeyNotFoundException($"Connection '{connectionId}' not found.");
    }
}

public sealed record TablePageResult(
    string ConnectionId,
    string? Schema,
    IReadOnlyList<TableInfo> Items,
    long Total,
    string? NextCursor);

public sealed record TableDescribeResult(TableDetails Details, string? Note);

public sealed record BatchDescribeResult(
    string ConnectionId,
    IReadOnlyList<TableDescribeResult> Tables,
    int TableCount,
    long ElapsedMs);
