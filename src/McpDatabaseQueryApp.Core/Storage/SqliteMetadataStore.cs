using System.Globalization;
using System.Text.Json;
using Dapper;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Core.Scripts;
using Microsoft.Data.Sqlite;

namespace McpDatabaseQueryApp.Core.Storage;

/// <summary>
/// SQLite-backed <see cref="IMetadataStore"/>. All read/write paths are
/// scoped by the ambient profile resolved through the
/// <see cref="IProfileContextAccessor"/>: no method may return a row whose
/// <c>profile_id</c> does not match the caller's current profile, and every
/// write stamps the ambient profile id into the inserted row. This is the
/// single chokepoint that enforces inter-profile isolation in the metadata
/// store.
/// </summary>
public sealed class SqliteMetadataStore : IMetadataStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly IProfileContextAccessor _profile;

    /// <summary>
    /// Creates a new <see cref="SqliteMetadataStore"/>.
    /// </summary>
    public SqliteMetadataStore(McpDatabaseQueryAppOptions options, IProfileContextAccessor profile)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(profile);
        _dbPath = PathResolver.Resolve(options.MetadataDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(_dbPath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        _profile = profile;
    }

    private string CurrentProfile => _profile.CurrentIdOrDefault.Value;

    /// <inheritdoc/>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA journal_mode=WAL;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition("PRAGMA foreign_keys=ON;", cancellationToken: cancellationToken)).ConfigureAwait(false);
        await SqliteSchema.EnsureCreatedAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<DatabaseRecord?> GetDatabaseAsync(string nameOrId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<DatabaseRow>(new CommandDefinition(
            "SELECT * FROM databases WHERE profile_id = @profile AND (name = @key OR id = @key) LIMIT 1;",
            new { key = nameOrId, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapRecord(row);
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<ConnectionDescriptor> Items, long Total)> ListDatabasesAsync(
        int offset,
        int limit,
        string? filter,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var normalizedFilter = string.IsNullOrWhiteSpace(filter) ? null : $"%{filter.Trim()}%";
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM databases WHERE profile_id = @profile AND (@f IS NULL OR name LIKE @f OR database_name LIKE @f);",
            new { f = normalizedFilter, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var rows = await connection.QueryAsync<DatabaseRow>(new CommandDefinition(
            """
            SELECT * FROM databases
            WHERE profile_id = @profile AND (@f IS NULL OR name LIKE @f OR database_name LIKE @f)
            ORDER BY name
            LIMIT @limit OFFSET @offset;
            """,
            new { f = normalizedFilter, limit, offset, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var items = rows.Select(MapDescriptor).ToList();
        return (items, total);
    }

    /// <inheritdoc/>
    public async Task<ConnectionDescriptor> UpsertDatabaseAsync(
        ConnectionDescriptor descriptor,
        byte[] passwordCipher,
        byte[] passwordNonce,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO databases(id, profile_id, name, provider, host, port, database_name, username,
                password_enc, password_nonce, ssl_mode, trust_server_cert, read_only,
                default_schema, tags_json, extra_json, created_at, updated_at)
            VALUES (@Id, @Profile, @Name, @Provider, @Host, @Port, @Database, @Username,
                @PasswordEnc, @PasswordNonce, @SslMode, @TrustServerCert, @ReadOnly,
                @DefaultSchema, @TagsJson, @ExtraJson, @CreatedAt, @UpdatedAt)
            ON CONFLICT(profile_id, name) DO UPDATE SET
                provider = excluded.provider,
                host = excluded.host,
                port = excluded.port,
                database_name = excluded.database_name,
                username = excluded.username,
                password_enc = excluded.password_enc,
                password_nonce = excluded.password_nonce,
                ssl_mode = excluded.ssl_mode,
                trust_server_cert = excluded.trust_server_cert,
                read_only = excluded.read_only,
                default_schema = excluded.default_schema,
                tags_json = excluded.tags_json,
                extra_json = excluded.extra_json,
                updated_at = excluded.updated_at;
            """,
            new
            {
                descriptor.Id,
                Profile = CurrentProfile,
                descriptor.Name,
                Provider = descriptor.Provider.ToString(),
                descriptor.Host,
                descriptor.Port,
                Database = descriptor.Database,
                descriptor.Username,
                PasswordEnc = passwordCipher,
                PasswordNonce = passwordNonce,
                SslMode = descriptor.SslMode,
                TrustServerCert = descriptor.TrustServerCertificate ? 1 : 0,
                ReadOnly = descriptor.ReadOnly ? 1 : 0,
                descriptor.DefaultSchema,
                TagsJson = JsonSerializer.Serialize(descriptor.Tags),
                ExtraJson = JsonSerializer.Serialize(descriptor.Extra ?? new Dictionary<string, string>()),
                CreatedAt = descriptor.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAt = descriptor.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return descriptor;
    }

    /// <inheritdoc/>
    public async Task<ConnectionDescriptor> UpdateDatabaseMetadataAsync(
        ConnectionDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE databases SET
                provider = @Provider,
                host = @Host,
                port = @Port,
                database_name = @Database,
                username = @Username,
                ssl_mode = @SslMode,
                trust_server_cert = @TrustServerCert,
                read_only = @ReadOnly,
                default_schema = @DefaultSchema,
                tags_json = @TagsJson,
                extra_json = @ExtraJson,
                updated_at = @UpdatedAt
            WHERE profile_id = @Profile AND (id = @Id OR name = @Name);
            """,
            new
            {
                descriptor.Id,
                Profile = CurrentProfile,
                descriptor.Name,
                Provider = descriptor.Provider.ToString(),
                descriptor.Host,
                descriptor.Port,
                Database = descriptor.Database,
                descriptor.Username,
                SslMode = descriptor.SslMode,
                TrustServerCert = descriptor.TrustServerCertificate ? 1 : 0,
                ReadOnly = descriptor.ReadOnly ? 1 : 0,
                descriptor.DefaultSchema,
                TagsJson = JsonSerializer.Serialize(descriptor.Tags),
                ExtraJson = JsonSerializer.Serialize(descriptor.Extra ?? new Dictionary<string, string>()),
                UpdatedAt = descriptor.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        if (rows == 0)
        {
            throw new KeyNotFoundException($"Database '{descriptor.Name}' not found.");
        }

        return descriptor;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteDatabaseAsync(string nameOrId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM databases WHERE profile_id = @profile AND (id = @key OR name = @key);",
            new { key = nameOrId, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc/>
    public async Task<ScriptRecord?> GetScriptAsync(string nameOrId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ScriptRow>(new CommandDefinition(
            "SELECT * FROM scripts WHERE profile_id = @profile AND (id = @key OR name = @key) LIMIT 1;",
            new { key = nameOrId, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapScript(row);
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<ScriptRecord> Items, long Total)> ListScriptsAsync(
        int offset,
        int limit,
        string? filter,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var normalizedFilter = string.IsNullOrWhiteSpace(filter) ? null : $"%{filter.Trim()}%";
        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM scripts WHERE profile_id = @profile AND (@f IS NULL OR name LIKE @f OR description LIKE @f);",
            new { f = normalizedFilter, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var rows = await connection.QueryAsync<ScriptRow>(new CommandDefinition(
            """
            SELECT * FROM scripts
            WHERE profile_id = @profile AND (@f IS NULL OR name LIKE @f OR description LIKE @f)
            ORDER BY name
            LIMIT @limit OFFSET @offset;
            """,
            new { f = normalizedFilter, limit, offset, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (rows.Select(MapScript).ToList(), total);
    }

    /// <inheritdoc/>
    public async Task<ScriptRecord> UpsertScriptAsync(ScriptRecord script, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO scripts(id, profile_id, name, description, provider, sql_text, destructive, tags_json, notes, parameters_json, created_at, updated_at)
            VALUES (@Id, @Profile, @Name, @Description, @Provider, @SqlText, @Destructive, @TagsJson, @Notes, @ParametersJson, @CreatedAt, @UpdatedAt)
            ON CONFLICT(profile_id, name) DO UPDATE SET
                description = excluded.description,
                provider = excluded.provider,
                sql_text = excluded.sql_text,
                destructive = excluded.destructive,
                tags_json = excluded.tags_json,
                notes = excluded.notes,
                parameters_json = excluded.parameters_json,
                updated_at = excluded.updated_at;
            """,
            new
            {
                script.Id,
                Profile = CurrentProfile,
                script.Name,
                script.Description,
                Provider = script.Provider?.ToString(),
                script.SqlText,
                Destructive = script.Destructive ? 1 : 0,
                TagsJson = JsonSerializer.Serialize(script.Tags),
                script.Notes,
                ParametersJson = JsonSerializer.Serialize(script.Parameters),
                CreatedAt = script.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAt = script.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return script;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteScriptAsync(string nameOrId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scripts WHERE profile_id = @profile AND (id = @key OR name = @key);",
            new { key = nameOrId, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc/>
    public async Task<ResultSetRecord?> GetResultSetAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ResultSetRow>(new CommandDefinition(
            "SELECT * FROM result_sets WHERE id = @id AND profile_id = @profile;",
            new { id, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapResultSet(row);
    }

    /// <inheritdoc/>
    public async Task InsertResultSetAsync(ResultSetRecord record, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO result_sets(id, profile_id, connection_id, column_meta_json, rows_path, total_rows, created_at, expires_at)
            VALUES (@Id, @Profile, @ConnectionId, @ColumnMeta, @RowsPath, @TotalRows, @CreatedAt, @ExpiresAt);
            """,
            new
            {
                record.Id,
                Profile = CurrentProfile,
                record.ConnectionId,
                ColumnMeta = JsonSerializer.Serialize(record.Columns),
                record.RowsPath,
                record.TotalRows,
                CreatedAt = record.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                ExpiresAt = record.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task PurgeExpiredResultSetsAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Janitor purge runs across all profiles by design (it is a global
        // background task that has no per-request profile scope). The
        // profile_id column is preserved for forensics but not filtered on.
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var expired = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT rows_path FROM result_sets WHERE expires_at <= @now;",
            new { now = now.ToString("O", CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        foreach (var path in expired)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // swallow — cache cleanup is best effort
            }
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM result_sets WHERE expires_at <= @now;",
            new { now = now.ToString("O", CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<NoteRecord?> GetNoteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<NoteRow>(new CommandDefinition(
            "SELECT * FROM notes WHERE profile_id = @profile AND target_type = @type AND target_path = @path LIMIT 1;",
            new { type = targetType.ToString(), path = targetPath, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapNote(row);
    }

    /// <inheritdoc/>
    public async Task<(IReadOnlyList<NoteRecord> Items, long Total)> ListNotesAsync(
        NoteTargetType? targetType,
        string? pathPrefix,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var typeFilter = targetType?.ToString();
        var normalizedPrefix = string.IsNullOrWhiteSpace(pathPrefix) ? null : $"{pathPrefix.Trim()}%";

        var total = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(*) FROM notes WHERE profile_id = @profile AND (@t IS NULL OR target_type = @t) AND (@p IS NULL OR target_path LIKE @p);",
            new { t = typeFilter, p = normalizedPrefix, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            """
            SELECT * FROM notes
            WHERE profile_id = @profile AND (@t IS NULL OR target_type = @t) AND (@p IS NULL OR target_path LIKE @p)
            ORDER BY target_type, target_path
            LIMIT @limit OFFSET @offset;
            """,
            new { t = typeFilter, p = normalizedPrefix, limit, offset, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return (rows.Select(MapNote).ToList(), total);
    }

    /// <inheritdoc/>
    public async Task<NoteRecord> UpsertNoteAsync(NoteRecord note, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO notes(id, profile_id, target_type, target_path, note_text, created_at, updated_at)
            VALUES (@Id, @Profile, @TargetType, @TargetPath, @NoteText, @CreatedAt, @UpdatedAt)
            ON CONFLICT(profile_id, target_type, target_path) DO UPDATE SET
                note_text = excluded.note_text,
                updated_at = excluded.updated_at;
            """,
            new
            {
                note.Id,
                Profile = CurrentProfile,
                TargetType = note.TargetType.ToString(),
                note.TargetPath,
                note.NoteText,
                CreatedAt = note.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                UpdatedAt = note.UpdatedAt.ToString("O", CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return note;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteNoteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notes WHERE profile_id = @profile AND target_type = @type AND target_path = @path;",
            new { type = targetType.ToString(), path = targetPath, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, NoteRecord>> GetNotesBulkAsync(
        NoteTargetType targetType,
        IReadOnlyList<string> targetPaths,
        CancellationToken cancellationToken)
    {
        if (targetPaths.Count == 0)
        {
            return new Dictionary<string, NoteRecord>();
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<NoteRow>(new CommandDefinition(
            "SELECT * FROM notes WHERE profile_id = @profile AND target_type = @type AND target_path IN @paths;",
            new { type = targetType.ToString(), paths = targetPaths, profile = CurrentProfile },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rows.Select(MapNote).ToDictionary(n => n.TargetPath, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static DatabaseRecord MapRecord(DatabaseRow row)
    {
        return new DatabaseRecord(MapDescriptor(row), row.password_enc, row.password_nonce);
    }

    private static ConnectionDescriptor MapDescriptor(DatabaseRow row)
    {
        return new ConnectionDescriptor
        {
            Id = row.id,
            Name = row.name,
            Provider = Enum.Parse<DatabaseKind>(row.provider, ignoreCase: true),
            Host = row.host,
            Port = row.port,
            Database = row.database_name,
            Username = row.username,
            SslMode = row.ssl_mode,
            TrustServerCertificate = row.trust_server_cert != 0,
            ReadOnly = row.read_only != 0,
            DefaultSchema = row.default_schema,
            Tags = JsonSerializer.Deserialize<List<string>>(row.tags_json) ?? [],
            Extra = JsonSerializer.Deserialize<Dictionary<string, string>>(row.extra_json) ?? new(),
            CreatedAt = DateTimeOffset.Parse(row.created_at, CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(row.updated_at, CultureInfo.InvariantCulture),
        };
    }

    private static ScriptRecord MapScript(ScriptRow row)
    {
        return new ScriptRecord
        {
            Id = row.id,
            Name = row.name,
            Description = row.description,
            Provider = row.provider is null ? null : Enum.Parse<DatabaseKind>(row.provider, ignoreCase: true),
            SqlText = row.sql_text,
            Destructive = row.destructive != 0,
            Tags = JsonSerializer.Deserialize<List<string>>(row.tags_json) ?? [],
            Notes = row.notes,
            Parameters = string.IsNullOrEmpty(row.parameters_json)
                ? []
                : JsonSerializer.Deserialize<List<ScriptParameter>>(row.parameters_json) ?? [],
            CreatedAt = DateTimeOffset.Parse(row.created_at, CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(row.updated_at, CultureInfo.InvariantCulture),
        };
    }

    private static ResultSetRecord MapResultSet(ResultSetRow row)
    {
        return new ResultSetRecord
        {
            Id = row.id,
            ConnectionId = row.connection_id,
            Columns = JsonSerializer.Deserialize<List<Providers.QueryColumn>>(row.column_meta_json) ?? [],
            RowsPath = row.rows_path,
            TotalRows = row.total_rows,
            CreatedAt = DateTimeOffset.Parse(row.created_at, CultureInfo.InvariantCulture),
            ExpiresAt = DateTimeOffset.Parse(row.expires_at, CultureInfo.InvariantCulture),
        };
    }

    private static NoteRecord MapNote(NoteRow row)
    {
        return new NoteRecord
        {
            Id = row.id,
            TargetType = Enum.Parse<NoteTargetType>(row.target_type, ignoreCase: true),
            TargetPath = row.target_path,
            NoteText = row.note_text,
            CreatedAt = DateTimeOffset.Parse(row.created_at, CultureInfo.InvariantCulture),
            UpdatedAt = DateTimeOffset.Parse(row.updated_at, CultureInfo.InvariantCulture),
        };
    }

#pragma warning disable IDE1006, SA1300, CA1812 // Dapper maps snake_case columns onto these properties via reflection.
    private sealed class DatabaseRow
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = ProfileId.DefaultValue;
        public string name { get; set; } = string.Empty;
        public string provider { get; set; } = string.Empty;
        public string host { get; set; } = string.Empty;
        public int? port { get; set; }
        public string database_name { get; set; } = string.Empty;
        public string username { get; set; } = string.Empty;
        public byte[] password_enc { get; set; } = Array.Empty<byte>();
        public byte[] password_nonce { get; set; } = Array.Empty<byte>();
        public string ssl_mode { get; set; } = string.Empty;
        public int trust_server_cert { get; set; }
        public int read_only { get; set; }
        public string? default_schema { get; set; }
        public string tags_json { get; set; } = "[]";
        public string extra_json { get; set; } = "{}";
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
    }

    private sealed class ScriptRow
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = ProfileId.DefaultValue;
        public string name { get; set; } = string.Empty;
        public string? description { get; set; }
        public string? provider { get; set; }
        public string sql_text { get; set; } = string.Empty;
        public int destructive { get; set; }
        public string tags_json { get; set; } = "[]";
        public string? notes { get; set; }
        public string parameters_json { get; set; } = "[]";
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
    }

    private sealed class ResultSetRow
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = ProfileId.DefaultValue;
        public string connection_id { get; set; } = string.Empty;
        public string column_meta_json { get; set; } = string.Empty;
        public string rows_path { get; set; } = string.Empty;
        public long total_rows { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string expires_at { get; set; } = string.Empty;
    }

    private sealed class NoteRow
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = ProfileId.DefaultValue;
        public string target_type { get; set; } = string.Empty;
        public string target_path { get; set; } = string.Empty;
        public string note_text { get; set; } = string.Empty;
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
    }
#pragma warning restore IDE1006, SA1300, CA1812
}
