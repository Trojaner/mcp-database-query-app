using System.Globalization;
using System.Text.Json;
using Dapper;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace McpDatabaseQueryApp.Core.Storage;

/// <summary>
/// SQLite-backed <see cref="IProfileStore"/>. Lives alongside
/// <see cref="SqliteMetadataStore"/> in the same database file rather than
/// being a method on <see cref="IMetadataStore"/> because profiles sit one
/// level above per-profile data: <see cref="IMetadataStore"/> filters its
/// rows by the ambient profile, whereas this store has to operate across
/// all profiles (lookup-by-identity, list, default-seed) without going
/// through that filter.
/// </summary>
public sealed class SqliteProfileStore : IProfileStore
{
    private readonly string _connectionString;

    /// <summary>
    /// Creates a new <see cref="SqliteProfileStore"/>.
    /// </summary>
    public SqliteProfileStore(McpDatabaseQueryAppOptions options)
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
    public async Task EnsureDefaultAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT OR IGNORE INTO profiles(profile_id, subject, issuer, name, created_at, status, metadata_json)
            VALUES (@id, @sub, NULL, @name, @t, @status, '{}');
            """,
            new
            {
                id = ProfileId.DefaultValue,
                sub = ProfileId.DefaultValue,
                name = "default",
                t = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                status = ProfileStatus.Active.ToString(),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<Profile> UpsertAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO profiles(profile_id, subject, issuer, name, created_at, status, metadata_json)
            VALUES (@profile_id, @subject, @issuer, @name, @created_at, @status, @metadata_json)
            ON CONFLICT(profile_id) DO UPDATE SET
                subject = excluded.subject,
                issuer = excluded.issuer,
                name = excluded.name,
                status = excluded.status,
                metadata_json = excluded.metadata_json;
            """,
            new
            {
                profile_id = profile.Id.Value,
                subject = profile.Subject,
                issuer = profile.Issuer,
                name = profile.Name,
                created_at = profile.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                status = profile.Status.ToString(),
                metadata_json = JsonSerializer.Serialize(profile.Metadata),
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return profile;
    }

    /// <inheritdoc/>
    public async Task<Profile?> GetAsync(ProfileId id, CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            "SELECT * FROM profiles WHERE profile_id = @id LIMIT 1;",
            new { id = id.Value },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    /// <inheritdoc/>
    public async Task<Profile?> FindByIdentityAsync(string? issuer, string subject, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await connection.QuerySingleOrDefaultAsync<ProfileRow>(new CommandDefinition(
            "SELECT * FROM profiles WHERE COALESCE(issuer, '') = COALESCE(@iss, '') AND subject = @sub LIMIT 1;",
            new { iss = issuer, sub = subject },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : Map(row);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Profile>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await connection.QueryAsync<ProfileRow>(new CommandDefinition(
            "SELECT * FROM profiles ORDER BY created_at;",
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows.Select(Map).ToList();
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(ProfileId id, CancellationToken cancellationToken)
    {
        if (id.IsDefault)
        {
            throw new InvalidOperationException("The default profile cannot be deleted.");
        }

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM databases    WHERE profile_id = @id;",
            new { id = id.Value },
            transaction: tx,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM scripts      WHERE profile_id = @id;",
            new { id = id.Value },
            transaction: tx,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM notes        WHERE profile_id = @id;",
            new { id = id.Value },
            transaction: tx,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM result_sets  WHERE profile_id = @id;",
            new { id = id.Value },
            transaction: tx,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        var rows = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM profiles WHERE profile_id = @id;",
            new { id = id.Value },
            transaction: tx,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows > 0;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static Profile Map(ProfileRow row)
    {
        return new Profile(
            new ProfileId(row.profile_id),
            row.name,
            row.subject,
            row.issuer,
            DateTimeOffset.Parse(row.created_at, CultureInfo.InvariantCulture),
            Enum.Parse<ProfileStatus>(row.status, ignoreCase: true),
            string.IsNullOrEmpty(row.metadata_json)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonSerializer.Deserialize<Dictionary<string, string>>(row.metadata_json) ?? new(StringComparer.Ordinal));
    }

#pragma warning disable IDE1006, SA1300, CA1812 // Dapper maps snake_case columns onto these properties via reflection.
    private sealed class ProfileRow
    {
        public string profile_id { get; set; } = string.Empty;
        public string subject { get; set; } = string.Empty;
        public string? issuer { get; set; }
        public string name { get; set; } = string.Empty;
        public string created_at { get; set; } = string.Empty;
        public string status { get; set; } = string.Empty;
        public string metadata_json { get; set; } = "{}";
    }
#pragma warning restore IDE1006, SA1300, CA1812
}
