using System.Data;
using System.Diagnostics;
using Dapper;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Providers.SqlServer;

public sealed class SqlServerConnection : IDatabaseConnection
{
    private readonly SqlConnection _connection;
    private readonly ILogger<SqlServerConnection> _logger;

    public SqlServerConnection(string id, ConnectionDescriptor descriptor, SqlConnection connection, ILogger<SqlServerConnection> logger)
    {
        Id = id;
        Descriptor = descriptor;
        _connection = connection;
        _logger = logger;
    }

    public string Id { get; }

    public DatabaseKind Kind => DatabaseKind.SqlServer;

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
        var limit = request.Limit ?? int.MaxValue;

        var sw = Stopwatch.StartNew();
        await using var command = _connection.CreateCommand();
        command.CommandText = request.Sql;
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
            SELECT s.name AS Name, p.name AS Owner
            FROM sys.schemas s
            LEFT JOIN sys.database_principals p ON s.principal_id = p.principal_id
            WHERE s.name NOT IN ('sys','INFORMATION_SCHEMA','guest','db_owner','db_accessadmin','db_securityadmin','db_ddladmin','db_backupoperator','db_datareader','db_datawriter','db_denydatareader','db_denydatawriter')
            ORDER BY s.name;
            """;
        var rows = await _connection.QueryAsync<(string Name, string? Owner)>(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new SchemaInfo(r.Name, r.Owner)).ToList();
    }

    public async Task<(IReadOnlyList<TableInfo> Items, long Total)> ListTablesAsync(string? schema, PageRequest page, CancellationToken cancellationToken)
    {
        const string countSql = """
            SELECT COUNT(*)
            FROM INFORMATION_SCHEMA.TABLES
            WHERE (@schema IS NULL OR TABLE_SCHEMA = @schema)
              AND (@filter IS NULL OR TABLE_NAME LIKE @filter);
            """;
        const string pageSql = """
            SELECT t.TABLE_SCHEMA AS schema_, t.TABLE_NAME AS name, t.TABLE_TYPE AS kind,
                   (SELECT p.rows FROM sys.partitions p
                    JOIN sys.objects o ON p.object_id = o.object_id
                    JOIN sys.schemas s ON o.schema_id = s.schema_id
                    WHERE s.name = t.TABLE_SCHEMA AND o.name = t.TABLE_NAME AND p.index_id IN (0,1)) AS row_estimate
            FROM INFORMATION_SCHEMA.TABLES t
            WHERE (@schema IS NULL OR t.TABLE_SCHEMA = @schema)
              AND (@filter IS NULL OR t.TABLE_NAME LIKE @filter)
            ORDER BY t.TABLE_SCHEMA, t.TABLE_NAME
            OFFSET @offset ROWS FETCH NEXT @limit ROWS ONLY;
            """;

        var filter = string.IsNullOrWhiteSpace(page.Filter) ? null : $"%{page.Filter.Trim()}%";
        var total = await _connection.ExecuteScalarAsync<long>(new CommandDefinition(countSql, new { schema, filter }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var rows = await _connection.QueryAsync(new CommandDefinition(pageSql, new { schema, filter, limit = page.Limit, offset = page.Offset }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var items = rows.Select(r =>
        {
            long? est = null;
            if (r.row_estimate is not null)
            {
                est = Convert.ToInt64(r.row_estimate);
            }

            return new TableInfo((string)r.schema_, (string)r.name, (string)r.kind, est, null);
        }).ToList();
        return (items, total);
    }

    public async Task<TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken)
    {
        const string infoSql = """
            SELECT t.TABLE_SCHEMA AS schema_, t.TABLE_NAME AS name, t.TABLE_TYPE AS kind
            FROM INFORMATION_SCHEMA.TABLES t
            WHERE t.TABLE_SCHEMA = @schema AND t.TABLE_NAME = @table;
            """;
        var row = await _connection.QuerySingleOrDefaultAsync(new CommandDefinition(infoSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Table {schema}.{table} not found.");

        var tableInfo = new TableInfo((string)row.schema_, (string)row.name, (string)row.kind, null, null);

        const string columnSql = """
            SELECT c.COLUMN_NAME AS name,
                   c.DATA_TYPE AS data_type,
                   CASE WHEN c.IS_NULLABLE = 'YES' THEN 1 ELSE 0 END AS is_nullable,
                   c.COLUMN_DEFAULT AS default_,
                   CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 1 ELSE 0 END AS is_pk,
                   COLUMNPROPERTY(OBJECT_ID(c.TABLE_SCHEMA + '.' + c.TABLE_NAME), c.COLUMN_NAME, 'IsIdentity') AS is_identity,
                   c.ORDINAL_POSITION AS ordinal,
                   c.CHARACTER_MAXIMUM_LENGTH AS max_length,
                   c.NUMERIC_PRECISION AS num_precision,
                   c.NUMERIC_SCALE AS num_scale
            FROM INFORMATION_SCHEMA.COLUMNS c
            LEFT JOIN (
                SELECT ku.TABLE_SCHEMA, ku.TABLE_NAME, ku.COLUMN_NAME
                FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
                JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME
                WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND ku.TABLE_SCHEMA = @schema AND ku.TABLE_NAME = @table
            ) pk ON pk.COLUMN_NAME = c.COLUMN_NAME
            WHERE c.TABLE_SCHEMA = @schema AND c.TABLE_NAME = @table
            ORDER BY c.ORDINAL_POSITION;
            """;
        var columnRows = await _connection.QueryAsync(new CommandDefinition(columnSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var columns = columnRows.Select(r => new ColumnInfo(
            (string)r.name,
            (string)r.data_type,
            Convert.ToInt32(r.is_nullable) == 1,
            r.default_ as string,
            Convert.ToInt32(r.is_pk) == 1,
            r.is_identity is not null && Convert.ToInt32(r.is_identity) == 1,
            (int?)r.ordinal,
            r.max_length is null ? null : Convert.ToInt32(r.max_length),
            r.num_precision is null ? null : Convert.ToInt32(r.num_precision),
            r.num_scale is null ? null : Convert.ToInt32(r.num_scale),
            null)).ToList();

        const string indexSql = """
            SELECT i.name AS name, i.is_unique AS is_unique, i.is_primary_key AS is_primary, i.type_desc AS method,
                   STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS cols
            FROM sys.indexes i
            JOIN sys.objects o ON o.object_id = i.object_id
            JOIN sys.schemas s ON o.schema_id = s.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE s.name = @schema AND o.name = @table AND i.name IS NOT NULL
            GROUP BY i.name, i.is_unique, i.is_primary_key, i.type_desc;
            """;
        var indexRows = await _connection.QueryAsync(new CommandDefinition(indexSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var indexes = indexRows.Select(r =>
        {
            var cols = ((string?)r.cols ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            return new IndexInfo((string)r.name, (bool)r.is_unique, (bool)r.is_primary, cols, r.method as string);
        }).ToList();

        const string fkSql = """
            SELECT fk.name AS name,
                   STRING_AGG(pc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id) AS cols,
                   rs.name AS ref_schema,
                   rt.name AS ref_table,
                   STRING_AGG(rc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id) AS ref_cols,
                   fk.update_referential_action_desc AS upd,
                   fk.delete_referential_action_desc AS del
            FROM sys.foreign_keys fk
            JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id
            JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
            JOIN sys.tables pt ON pt.object_id = fk.parent_object_id
            JOIN sys.schemas ps ON ps.schema_id = pt.schema_id
            JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            WHERE ps.name = @schema AND pt.name = @table
            GROUP BY fk.name, rs.name, rt.name, fk.update_referential_action_desc, fk.delete_referential_action_desc;
            """;
        var fkRows = await _connection.QueryAsync(new CommandDefinition(fkSql, new { schema, table }, cancellationToken: cancellationToken)).ConfigureAwait(false);
        var fks = fkRows.Select(r =>
        {
            var cols = ((string?)r.cols ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            var refCols = ((string?)r.ref_cols ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries);
            return new ForeignKeyInfo((string)r.name, cols, (string)r.ref_schema, (string)r.ref_table, refCols, (string)r.upd, (string)r.del);
        }).ToList();

        return new TableDetails(tableInfo, columns, indexes, fks);
    }

    public async Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT p.name AS Name,
                   CASE WHEN p.type IN ('S','U') THEN 1 ELSE 0 END AS IsLogin,
                   CASE WHEN p.name = 'sa' OR IS_SRVROLEMEMBER('sysadmin', p.name) = 1 THEN 1 ELSE 0 END AS IsSuper
            FROM sys.database_principals p
            WHERE p.type IN ('S','U','G','R') AND p.name NOT LIKE '##%'
            ORDER BY p.name;
            """;
        var rows = await _connection.QueryAsync(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new RoleInfo((string)r.Name, Convert.ToInt32(r.IsLogin) == 1, Convert.ToInt32(r.IsSuper) == 1, Array.Empty<string>())).ToList();
    }

    public async Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT name AS Name,
                   SUSER_SNAME(owner_sid) AS Owner,
                   collation_name AS Encoding,
                   NULL AS SizeBytes
            FROM sys.databases
            WHERE state_desc = 'ONLINE'
            ORDER BY name;
            """;
        var rows = await _connection.QueryAsync<(string Name, string? Owner, string? Encoding, long? SizeBytes)>(new CommandDefinition(sql, cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(r => new DatabaseInfo(r.Name, r.Owner, r.Encoding, r.SizeBytes)).ToList();
    }

    public async Task<ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
    {
        await using var showplan = _connection.CreateCommand();
        showplan.CommandText = "SET SHOWPLAN_XML ON;";
        await showplan.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = sql;
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

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var plan = reader.GetString(0);
                return new ExplainResult("showplan-xml", plan);
            }

            return new ExplainResult("showplan-xml", string.Empty);
        }
        finally
        {
            await using var reset = _connection.CreateCommand();
            reset.CommandText = "SET SHOWPLAN_XML OFF;";
            await reset.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
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
