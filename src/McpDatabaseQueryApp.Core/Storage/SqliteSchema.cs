using System.Globalization;
using Dapper;
using Microsoft.Data.Sqlite;

namespace McpDatabaseQueryApp.Core.Storage;

public static class SqliteSchema
{
    public const int CurrentVersion = 3;

    public static async Task EnsureCreatedAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS _schema_version (
                version INTEGER NOT NULL PRIMARY KEY,
                applied_at TEXT NOT NULL
            );
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var version = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT MAX(version) FROM _schema_version;",
            cancellationToken: cancellationToken)).ConfigureAwait(false) ?? 0;

        if (version < 1)
        {
            await ApplyV1Async(connection, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO _schema_version (version, applied_at) VALUES (1, @t);",
                new { t = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (version < 2)
        {
            await ApplyV2Async(connection, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO _schema_version (version, applied_at) VALUES (2, @t);",
                new { t = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }

        if (version < 3)
        {
            await ApplyV3Async(connection, cancellationToken).ConfigureAwait(false);
            await connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO _schema_version (version, applied_at) VALUES (3, @t);",
                new { t = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
                cancellationToken: cancellationToken)).ConfigureAwait(false);
        }
    }

    private static async Task ApplyV1Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS databases (
                id                  TEXT PRIMARY KEY,
                name                TEXT NOT NULL UNIQUE,
                provider            TEXT NOT NULL,
                host                TEXT NOT NULL,
                port                INTEGER,
                database_name       TEXT NOT NULL,
                username            TEXT NOT NULL,
                password_enc        BLOB NOT NULL,
                password_nonce      BLOB NOT NULL,
                ssl_mode            TEXT NOT NULL,
                trust_server_cert   INTEGER NOT NULL,
                read_only           INTEGER NOT NULL,
                default_schema      TEXT,
                tags_json           TEXT NOT NULL DEFAULT '[]',
                extra_json          TEXT NOT NULL DEFAULT '{}',
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_databases_provider ON databases(provider);

            CREATE TABLE IF NOT EXISTS scripts (
                id            TEXT PRIMARY KEY,
                name          TEXT NOT NULL UNIQUE,
                description   TEXT,
                provider      TEXT,
                sql_text      TEXT NOT NULL,
                destructive   INTEGER NOT NULL,
                tags_json     TEXT NOT NULL DEFAULT '[]',
                created_at    TEXT NOT NULL,
                updated_at    TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_scripts_name ON scripts(name);

            CREATE TABLE IF NOT EXISTS result_sets (
                id               TEXT PRIMARY KEY,
                connection_id    TEXT NOT NULL,
                column_meta_json TEXT NOT NULL,
                rows_path        TEXT NOT NULL,
                total_rows       INTEGER NOT NULL,
                created_at       TEXT NOT NULL,
                expires_at       TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_result_sets_expires ON result_sets(expires_at);
            """;

        await connection.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task ApplyV2Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS notes (
                id              TEXT PRIMARY KEY,
                target_type     TEXT NOT NULL,
                target_path     TEXT NOT NULL,
                note_text       TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                UNIQUE(target_type, target_path)
            );
            CREATE INDEX IF NOT EXISTS idx_notes_target ON notes(target_type, target_path);
            """;

        await connection.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task ApplyV3Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string ddl = """
            ALTER TABLE scripts ADD COLUMN notes TEXT;
            ALTER TABLE scripts ADD COLUMN parameters_json TEXT NOT NULL DEFAULT '[]';
            """;

        await connection.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
