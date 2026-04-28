using System.Globalization;
using Dapper;
using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace McpDatabaseQueryApp.Core.Storage;

public static class SqliteSchema
{
    public const int CurrentVersion = 5;

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
            await StampAsync(connection, 1, cancellationToken).ConfigureAwait(false);
        }

        if (version < 2)
        {
            await ApplyV2Async(connection, cancellationToken).ConfigureAwait(false);
            await StampAsync(connection, 2, cancellationToken).ConfigureAwait(false);
        }

        if (version < 3)
        {
            await ApplyV3Async(connection, cancellationToken).ConfigureAwait(false);
            await StampAsync(connection, 3, cancellationToken).ConfigureAwait(false);
        }

        if (version < 4)
        {
            await ApplyV4Async(connection, cancellationToken).ConfigureAwait(false);
            await StampAsync(connection, 4, cancellationToken).ConfigureAwait(false);
        }

        if (version < 5)
        {
            await ApplyV5Async(connection, cancellationToken).ConfigureAwait(false);
            await StampAsync(connection, 5, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task StampAsync(SqliteConnection connection, int version, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO _schema_version (version, applied_at) VALUES (@v, @t);",
            new { v = version, t = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
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

    /// <summary>
    /// v4: introduces the <c>profiles</c> table, adds a <c>profile_id</c>
    /// column to every per-profile table, backfills existing rows to the
    /// built-in <c>default</c> profile, and replaces the previously-global
    /// uniqueness constraints on <c>databases.name</c>, <c>scripts.name</c>,
    /// and <c>notes.(target_type, target_path)</c> with profile-scoped ones.
    /// </summary>
    private static async Task ApplyV4Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS profiles (
                profile_id   TEXT PRIMARY KEY,
                subject      TEXT NOT NULL,
                issuer       TEXT,
                name         TEXT NOT NULL,
                created_at   TEXT NOT NULL,
                status       TEXT NOT NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}'
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_profiles_identity
                ON profiles(COALESCE(issuer, ''), subject);

            -- databases: drop UNIQUE(name), add profile_id, scope uniqueness per profile.
            CREATE TABLE databases__v4 (
                id                  TEXT PRIMARY KEY,
                profile_id          TEXT NOT NULL DEFAULT 'default',
                name                TEXT NOT NULL,
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
                updated_at          TEXT NOT NULL,
                UNIQUE(profile_id, name)
            );
            INSERT INTO databases__v4(id, profile_id, name, provider, host, port, database_name, username,
                password_enc, password_nonce, ssl_mode, trust_server_cert, read_only,
                default_schema, tags_json, extra_json, created_at, updated_at)
            SELECT id, 'default', name, provider, host, port, database_name, username,
                password_enc, password_nonce, ssl_mode, trust_server_cert, read_only,
                default_schema, tags_json, extra_json, created_at, updated_at
            FROM databases;
            DROP TABLE databases;
            ALTER TABLE databases__v4 RENAME TO databases;
            CREATE INDEX IF NOT EXISTS idx_databases_provider ON databases(provider);
            CREATE INDEX IF NOT EXISTS idx_databases_profile ON databases(profile_id);

            -- scripts: drop UNIQUE(name), add profile_id, scope uniqueness per profile.
            CREATE TABLE scripts__v4 (
                id              TEXT PRIMARY KEY,
                profile_id      TEXT NOT NULL DEFAULT 'default',
                name            TEXT NOT NULL,
                description     TEXT,
                provider        TEXT,
                sql_text        TEXT NOT NULL,
                destructive     INTEGER NOT NULL,
                tags_json       TEXT NOT NULL DEFAULT '[]',
                notes           TEXT,
                parameters_json TEXT NOT NULL DEFAULT '[]',
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                UNIQUE(profile_id, name)
            );
            INSERT INTO scripts__v4(id, profile_id, name, description, provider, sql_text, destructive,
                tags_json, notes, parameters_json, created_at, updated_at)
            SELECT id, 'default', name, description, provider, sql_text, destructive,
                tags_json, notes, parameters_json, created_at, updated_at
            FROM scripts;
            DROP TABLE scripts;
            ALTER TABLE scripts__v4 RENAME TO scripts;
            CREATE INDEX IF NOT EXISTS idx_scripts_name ON scripts(profile_id, name);

            -- notes: drop UNIQUE(target_type, target_path), add profile_id, scope per profile.
            CREATE TABLE notes__v4 (
                id              TEXT PRIMARY KEY,
                profile_id      TEXT NOT NULL DEFAULT 'default',
                target_type     TEXT NOT NULL,
                target_path     TEXT NOT NULL,
                note_text       TEXT NOT NULL,
                created_at      TEXT NOT NULL,
                updated_at      TEXT NOT NULL,
                UNIQUE(profile_id, target_type, target_path)
            );
            INSERT INTO notes__v4(id, profile_id, target_type, target_path, note_text, created_at, updated_at)
            SELECT id, 'default', target_type, target_path, note_text, created_at, updated_at
            FROM notes;
            DROP TABLE notes;
            ALTER TABLE notes__v4 RENAME TO notes;
            CREATE INDEX IF NOT EXISTS idx_notes_target ON notes(profile_id, target_type, target_path);

            -- result_sets: simple add-column; existing rows backfill to 'default'.
            ALTER TABLE result_sets ADD COLUMN profile_id TEXT NOT NULL DEFAULT 'default';
            CREATE INDEX IF NOT EXISTS idx_result_sets_profile ON result_sets(profile_id);
            """;

        await connection.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: cancellationToken)).ConfigureAwait(false);

        // Seed the built-in default profile.
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

    /// <summary>
    /// v5: introduces the <c>acl_entries</c> table. Each row binds a profile
    /// to an operation set on a hierarchical object scope (host/port/db/
    /// schema/table/column, nullable fields = wildcards) with an Allow/Deny
    /// effect and a priority ordering. Ownership cascades from
    /// <c>profiles</c> so deleting a profile removes its ACL.
    /// </summary>
    private static async Task ApplyV5Async(SqliteConnection connection, CancellationToken cancellationToken)
    {
        const string ddl = """
            CREATE TABLE IF NOT EXISTS acl_entries (
                id                  TEXT NOT NULL PRIMARY KEY,
                profile_id          TEXT NOT NULL,
                subject_kind        TEXT NOT NULL,
                host                TEXT NULL,
                port                INTEGER NULL,
                database_name       TEXT NULL,
                schema_name         TEXT NULL,
                table_name          TEXT NULL,
                column_name         TEXT NULL,
                allowed_operations  INTEGER NOT NULL,
                effect              TEXT NOT NULL,
                priority            INTEGER NOT NULL DEFAULT 0,
                description         TEXT NULL,
                created_at          TEXT NOT NULL,
                updated_at          TEXT NOT NULL,
                FOREIGN KEY (profile_id) REFERENCES profiles(profile_id) ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS idx_acl_entries_profile ON acl_entries(profile_id, priority DESC);
            """;

        await connection.ExecuteAsync(new CommandDefinition(ddl, cancellationToken: cancellationToken)).ConfigureAwait(false);
    }
}
