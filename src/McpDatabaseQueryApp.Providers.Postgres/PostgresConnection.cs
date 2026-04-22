using System.Data;
using System.Diagnostics;
using Dapper;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace McpDatabaseQueryApp.Providers.Postgres;

public sealed class PostgresConnection : IDatabaseConnection
{
    private readonly NpgsqlConnection _connection;
    private readonly ILogger<PostgresConnection> _logger;

    public PostgresConnection(string id, ConnectionDescriptor descriptor, NpgsqlConnection connection, ILogger<PostgresConnection> logger)
    {
        Id = id;
        Descriptor = descriptor;
        _connection = connection;
        _logger = logger;
    }

    public string Id { get; }

    public DatabaseKind Kind => DatabaseKind.Postgres;

    public ConnectionDescriptor Descriptor { get; }

    public bool IsReadOnly => Descriptor.ReadOnly;

    public async Task PingAsync(CancellationToken cancellationToken)
    {
        await _connection.ExecuteScalarAsync<int>(new CommandDefinition("SELECT 1;", cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<QueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var parameters = ToParameters(request.Parameters);
        var sql = request.Sql;
        var limit = request.Limit ?? int.MaxValue;

        var sw = Stopwatch.StartNew();
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        if (request.TimeoutSeconds is { } timeout)
        {
            command.CommandTimeout = timeout;
        }

        if (parameters is not null)
        {
            foreach (var kv in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = kv.Key;
                parameter.Value = kv.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);

        var columns = new List<QueryColumn>();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            columns.Add(new QueryColumn(reader.GetName(i), reader.GetDataTypeName(i), i));
        }

        var rows = new List<IReadOnlyList<object?>>();
        long totalRead = 0;
        var truncated = false;
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            totalRead++;
            if (rows.Count >= limit)
            {
                truncated = true;
                continue;
            }

            var values = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                var value = reader.GetValue(i);
                values[i] = value is DBNull ? null : value;
            }

            rows.Add(values);
        }

        sw.Stop();
        return new QueryResult(columns, rows, rows.Count, truncated, totalRead, sw.ElapsedMilliseconds);
    }

    public async Task<long> ExecuteNonQueryAsync(NonQueryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (IsReadOnly)
        {
            throw new InvalidOperationException("Connection is read-only.");
        }

        var parameters = ToParameters(request.Parameters);
        return await _connection.ExecuteAsync(new CommandDefinition(
            request.Sql,
            parameters is null ? null : new DynamicParameters(parameters),
            commandTimeout: request.TimeoutSeconds,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT n.nspname AS name, r.rolname AS owner
            FROM pg_namespace n
            LEFT JOIN pg_roles r ON n.nspowner = r.oid
            WHERE n.nspname NOT IN ('pg_toast', 'pg_catalog', 'information_schema')
              AND n.nspname NOT LIKE 'pg_temp_%'
              AND n.nspname NOT LIKE 'pg_toast_temp_%'
            ORDER BY n.nspname;
            """;
        var rows = await _connection.QueryAsync<(string name, string? owner)>(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new SchemaInfo(r.name, r.owner)).ToList();
    }

    public async Task<(IReadOnlyList<TableInfo> Items, long Total)> ListTablesAsync(string? schema, PageRequest page, CancellationToken cancellationToken)
    {
        const string countSql = """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE (@schema IS NULL OR table_schema = @schema)
              AND table_schema NOT IN ('pg_catalog', 'information_schema')
              AND (@filter IS NULL OR table_name ILIKE @filter);
            """;
        const string pageSql = """
            SELECT t.table_schema AS schema_, t.table_name AS name, t.table_type AS kind, c.reltuples::bigint AS row_estimate, obj_description(c.oid) AS comment
            FROM information_schema.tables t
            LEFT JOIN pg_catalog.pg_class c ON c.relname = t.table_name
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace AND n.nspname = t.table_schema
            WHERE (@schema IS NULL OR t.table_schema = @schema)
              AND t.table_schema NOT IN ('pg_catalog', 'information_schema')
              AND (@filter IS NULL OR t.table_name ILIKE @filter)
            ORDER BY t.table_schema, t.table_name
            LIMIT @limit OFFSET @offset;
            """;

        var filter = string.IsNullOrWhiteSpace(page.Filter) ? null : $"%{page.Filter.Trim()}%";
        var total = await _connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, new { schema, filter }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var rows = await _connection.QueryAsync(new CommandDefinition(pageSql, new { schema, filter, limit = page.Limit, offset = page.Offset }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var items = rows
            .Select(r => new TableInfo(
                (string)r.schema_,
                (string)r.name,
                (string)r.kind,
                r.row_estimate as long?,
                r.comment as string))
            .ToList();
        return (items, total);
    }

    public async Task<TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken)
    {
        const string infoSql = """
            SELECT t.table_schema AS schema_, t.table_name AS name, t.table_type AS kind, c.reltuples::bigint AS row_estimate, obj_description(c.oid) AS comment
            FROM information_schema.tables t
            LEFT JOIN pg_catalog.pg_class c ON c.relname = t.table_name
            LEFT JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace AND n.nspname = t.table_schema
            WHERE t.table_schema = @schema AND t.table_name = @table
            LIMIT 1;
            """;
        var tableRow = await _connection.QuerySingleOrDefaultAsync(new CommandDefinition(infoSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Table {schema}.{table} not found.");

        var tableInfo = new TableInfo(
            (string)tableRow.schema_,
            (string)tableRow.name,
            (string)tableRow.kind,
            tableRow.row_estimate as long?,
            tableRow.comment as string);

        const string columnSql = """
            SELECT c.column_name AS name,
                   c.data_type AS data_type,
                   (c.is_nullable = 'YES') AS is_nullable,
                   c.column_default AS default_,
                   (CASE WHEN pk.column_name IS NOT NULL THEN TRUE ELSE FALSE END) AS is_pk,
                   (CASE WHEN c.is_identity = 'YES' THEN TRUE ELSE FALSE END) AS is_identity,
                   c.ordinal_position AS ordinal,
                   c.character_maximum_length AS max_length,
                   c.numeric_precision AS num_precision,
                   c.numeric_scale AS num_scale,
                   col_description(pc.oid, c.ordinal_position::int) AS comment
            FROM information_schema.columns c
            LEFT JOIN pg_catalog.pg_class pc ON pc.relname = c.table_name
            LEFT JOIN pg_catalog.pg_namespace pn ON pn.oid = pc.relnamespace AND pn.nspname = c.table_schema
            LEFT JOIN (
                SELECT kcu.column_name
                FROM information_schema.key_column_usage kcu
                JOIN information_schema.table_constraints tc ON tc.constraint_name = kcu.constraint_name
                WHERE tc.constraint_type = 'PRIMARY KEY'
                  AND kcu.table_schema = @schema
                  AND kcu.table_name = @table
            ) pk ON pk.column_name = c.column_name
            WHERE c.table_schema = @schema AND c.table_name = @table
            ORDER BY c.ordinal_position;
            """;
        var columnRows = await _connection.QueryAsync(new CommandDefinition(columnSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var columns = columnRows.Select(r => new ColumnInfo(
            (string)r.name,
            (string)r.data_type,
            (bool)r.is_nullable,
            r.default_ as string,
            (bool)r.is_pk,
            (bool)r.is_identity,
            (int?)r.ordinal,
            (int?)r.max_length,
            (int?)r.num_precision,
            (int?)r.num_scale,
            r.comment as string)).ToList();

        const string indexSql = """
            SELECT i.relname AS name,
                   idx.indisunique AS is_unique,
                   idx.indisprimary AS is_primary,
                   am.amname AS method,
                   array_agg(a.attname ORDER BY x.ord) AS columns
            FROM pg_index idx
            JOIN pg_class t ON t.oid = idx.indrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class i ON i.oid = idx.indexrelid
            JOIN pg_am am ON am.oid = i.relam
            JOIN LATERAL unnest(idx.indkey) WITH ORDINALITY AS x(attnum, ord) ON TRUE
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = x.attnum
            WHERE n.nspname = @schema AND t.relname = @table
            GROUP BY i.relname, idx.indisunique, idx.indisprimary, am.amname;
            """;
        var indexRows = await _connection.QueryAsync(new CommandDefinition(indexSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var indexes = indexRows.Select(r =>
        {
            string[] cols = ((IEnumerable<object>)r.columns).Select(o => o?.ToString() ?? string.Empty).ToArray();
            return new IndexInfo((string)r.name, (bool)r.is_unique, (bool)r.is_primary, cols, r.method as string);
        }).ToList();

        const string fkSql = """
            SELECT c.conname AS name,
                   (SELECT array_agg(att.attname ORDER BY u.ord)
                    FROM unnest(c.conkey) WITH ORDINALITY u(attnum, ord)
                    JOIN pg_attribute att ON att.attrelid = c.conrelid AND att.attnum = u.attnum) AS cols,
                   cn.nspname AS ref_schema,
                   ct.relname AS ref_table,
                   (SELECT array_agg(att.attname ORDER BY u.ord)
                    FROM unnest(c.confkey) WITH ORDINALITY u(attnum, ord)
                    JOIN pg_attribute att ON att.attrelid = c.confrelid AND att.attnum = u.attnum) AS ref_cols,
                   c.confupdtype::text AS upd,
                   c.confdeltype::text AS del
            FROM pg_constraint c
            JOIN pg_class t ON t.oid = c.conrelid
            JOIN pg_namespace n ON n.oid = t.relnamespace
            JOIN pg_class ct ON ct.oid = c.confrelid
            JOIN pg_namespace cn ON cn.oid = ct.relnamespace
            WHERE c.contype = 'f' AND n.nspname = @schema AND t.relname = @table;
            """;
        var fkRows = await _connection.QueryAsync(new CommandDefinition(fkSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var fks = fkRows.Select(r =>
        {
            string[] cols = ((IEnumerable<object>)r.cols).Select(o => o?.ToString() ?? string.Empty).ToArray();
            string[] refCols = ((IEnumerable<object>)r.ref_cols).Select(o => o?.ToString() ?? string.Empty).ToArray();
            return new ForeignKeyInfo((string)r.name, cols, (string)r.ref_schema, (string)r.ref_table, refCols, (string)r.upd, (string)r.del);
        }).ToList();

        return new TableDetails(tableInfo, columns, indexes, fks);
    }

    public async Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT r.rolname AS name, r.rolcanlogin AS can_login, r.rolsuper AS is_super,
                   ARRAY(
                       SELECT b.rolname
                       FROM pg_auth_members m JOIN pg_roles b ON m.roleid = b.oid
                       WHERE m.member = r.oid
                   ) AS member_of
            FROM pg_roles r
            ORDER BY r.rolname;
            """;
        var rows = await _connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r =>
        {
            string[] memberOf = ((IEnumerable<object>)r.member_of).Select(o => o?.ToString() ?? string.Empty).ToArray();
            return new RoleInfo((string)r.name, (bool)r.can_login, (bool)r.is_super, memberOf);
        }).ToList();
    }

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT d.datname AS name, r.rolname AS owner, pg_encoding_to_char(d.encoding) AS encoding, pg_database_size(d.datname) AS size_bytes
            FROM pg_database d
            LEFT JOIN pg_roles r ON d.datdba = r.oid
            WHERE NOT d.datistemplate
            ORDER BY d.datname;
            """;
        var rows = await _connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new DatabaseInfo((string)r.name, r.owner as string, r.encoding as string, r.size_bytes as long?)).ToList();
    }

    public async Task<ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        var explainSql = $"EXPLAIN (FORMAT JSON) {sql}";
        await using var command = _connection.CreateCommand();
        command.CommandText = explainSql;
        if (parameters is not null)
        {
            foreach (var kv in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = kv.Key;
                parameter.Value = kv.Value ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return new ExplainResult("json", result?.ToString() ?? "[]");
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    private static Dictionary<string, object?>? ToParameters(IReadOnlyDictionary<string, object?>? source)
    {
        if (source is null || source.Count == 0)
        {
            return null;
        }

        var copy = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var kv in source)
        {
            var name = kv.Key.StartsWith('@') ? kv.Key : "@" + kv.Key;
            copy[name] = kv.Value;
        }

        return copy;
    }
}
