using System.Globalization;
using Dapper;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace McpDatabaseQueryApp.Core.Storage;

/// <summary>
/// SQLite-backed <see cref="IAclStore"/>. Lives alongside
/// <see cref="SqliteMetadataStore"/> in the same database file. Unlike the
/// metadata store this store accepts the target profile id as an explicit
/// argument: ACL writes are driven by the operator-facing REST API which
/// administers ACLs across profiles, so the ambient profile context is not
/// the right scope.
/// </summary>
public sealed class SqliteAclStore : IAclStore
{
    private readonly string _connectionString;

    /// <summary>Creates a new <see cref="SqliteAclStore"/>.</summary>
    public SqliteAclStore(McpDatabaseQueryAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var path = PathResolver.Resolve(options.MetadataDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<AclEntry>> ListAsync(ProfileId profile, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<AclRow>(new CommandDefinition(
            "SELECT * FROM acl_entries WHERE profile_id = @profile ORDER BY priority DESC, id ASC;",
            new { profile = profile.Value },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    /// <inheritdoc/>
    public async Task<AclEntry?> GetAsync(AclEntryId id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<AclRow>(new CommandDefinition(
            "SELECT * FROM acl_entries WHERE id = @id LIMIT 1;",
            new { id = id.Value },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    /// <inheritdoc/>
    public async Task<AclEntry> UpsertAsync(AclEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO acl_entries(id, profile_id, subject_kind, host, port, database_name,
                schema_name, table_name, column_name, allowed_operations, effect, priority,
                description, created_at, updated_at)
            VALUES (@Id, @ProfileId, @SubjectKind, @Host, @Port, @DatabaseName,
                @Schema, @Table, @Column, @AllowedOperations, @Effect, @Priority,
                @Description, @Now, @Now)
            ON CONFLICT(id) DO UPDATE SET
                profile_id = excluded.profile_id,
                subject_kind = excluded.subject_kind,
                host = excluded.host,
                port = excluded.port,
                database_name = excluded.database_name,
                schema_name = excluded.schema_name,
                table_name = excluded.table_name,
                column_name = excluded.column_name,
                allowed_operations = excluded.allowed_operations,
                effect = excluded.effect,
                priority = excluded.priority,
                description = excluded.description,
                updated_at = excluded.updated_at;
            """,
            new
            {
                Id = entry.Id.Value,
                ProfileId = entry.ProfileId.Value,
                SubjectKind = entry.SubjectKind.ToString(),
                entry.Scope.Host,
                entry.Scope.Port,
                DatabaseName = entry.Scope.DatabaseName,
                Schema = entry.Scope.Schema,
                Table = entry.Scope.Table,
                Column = entry.Scope.Column,
                AllowedOperations = (long)entry.AllowedOperations,
                Effect = entry.Effect.ToString(),
                entry.Priority,
                entry.Description,
                Now = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return entry;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(ProfileId profile, AclEntryId id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM acl_entries WHERE id = @id AND profile_id = @profile;",
            new { id = id.Value, profile = profile.Value },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows > 0;
    }

    /// <inheritdoc/>
    public async Task ReplaceAllAsync(ProfileId profile, IReadOnlyList<AclEntry> entries, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entries);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM acl_entries WHERE profile_id = @profile;",
            new { profile = profile.Value },
            transaction: tx,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var entry in entries)
        {
            if (entry.ProfileId.Value != profile.Value)
            {
                throw new InvalidOperationException(
                    $"ACL entry '{entry.Id.Value}' belongs to profile '{entry.ProfileId.Value}' but ReplaceAllAsync was called for '{profile.Value}'.");
            }

            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO acl_entries(id, profile_id, subject_kind, host, port, database_name,
                    schema_name, table_name, column_name, allowed_operations, effect, priority,
                    description, created_at, updated_at)
                VALUES (@Id, @ProfileId, @SubjectKind, @Host, @Port, @DatabaseName,
                    @Schema, @Table, @Column, @AllowedOperations, @Effect, @Priority,
                    @Description, @Now, @Now);
                """,
                new
                {
                    Id = entry.Id.Value,
                    ProfileId = entry.ProfileId.Value,
                    SubjectKind = entry.SubjectKind.ToString(),
                    entry.Scope.Host,
                    entry.Scope.Port,
                    DatabaseName = entry.Scope.DatabaseName,
                    Schema = entry.Scope.Schema,
                    Table = entry.Scope.Table,
                    Column = entry.Scope.Column,
                    AllowedOperations = (long)entry.AllowedOperations,
                    Effect = entry.Effect.ToString(),
                    entry.Priority,
                    entry.Description,
                    Now = now,
                },
                transaction: tx,
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static AclEntry Map(AclRow row)
    {
        return new AclEntry(
            new AclEntryId(row.id),
            new ProfileId(row.profile_id),
            Enum.Parse<AclSubjectKind>(row.subject_kind, ignoreCase: true),
            new AclObjectScope(
                Host: row.host,
                Port: row.port,
                DatabaseName: row.database_name,
                Schema: row.schema_name,
                Table: row.table_name,
                Column: row.column_name),
            (AclOperation)row.allowed_operations,
            Enum.Parse<AclEffect>(row.effect, ignoreCase: true),
            (int)row.priority,
            row.description);
    }

#pragma warning disable IDE1006, SA1300, CA1812 // Dapper maps snake_case columns onto these properties via reflection.
    private sealed class AclRow
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = string.Empty;
        public string subject_kind { get; set; } = string.Empty;
        public string? host { get; set; }
        public int? port { get; set; }
        public string? database_name { get; set; }
        public string? schema_name { get; set; }
        public string? table_name { get; set; }
        public string? column_name { get; set; }
        public long allowed_operations { get; set; }
        public string effect { get; set; } = string.Empty;
        public long priority { get; set; }
        public string? description { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
    }
#pragma warning restore IDE1006, SA1300, CA1812
}
