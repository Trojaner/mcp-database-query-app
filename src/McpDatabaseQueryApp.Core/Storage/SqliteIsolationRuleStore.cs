using System.Globalization;
using System.Text.Json;
using Dapper;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.Data.Sqlite;

namespace McpDatabaseQueryApp.Core.Storage;

/// <summary>
/// SQLite-backed <see cref="IIsolationRuleStore"/>. Stores dynamic rules in
/// the <c>isolation_rules</c> table (introduced in schema v6) and overlays
/// the in-memory <see cref="StaticIsolationRuleRegistry"/> on every read.
/// </summary>
/// <remarks>
/// The static + dynamic merge happens in <see cref="ListAsync"/>: dynamic
/// rows are filtered by profile and connection scope, the static rules from
/// the registry are concatenated, and the union is sorted by descending
/// priority then by id. <see cref="UpsertAsync"/> and
/// <see cref="DeleteAsync"/> refuse to mutate static rules.
/// </remarks>
public sealed class SqliteIsolationRuleStore : IIsolationRuleStore
{
    private readonly string _connectionString;
    private readonly StaticIsolationRuleRegistry _statics;

    /// <summary>
    /// Creates a new <see cref="SqliteIsolationRuleStore"/>.
    /// </summary>
    public SqliteIsolationRuleStore(McpDatabaseQueryAppOptions options, StaticIsolationRuleRegistry statics)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(statics);
        var path = PathResolver.Resolve(options.MetadataDbPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
        _statics = statics;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<IsolationRule>> ListAsync(
        ProfileId profileId,
        ConnectionDescriptor connection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);

        await using var sqlite = await OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = await sqlite.QueryAsync<IsolationRuleRow>(new CommandDefinition(
            """
            SELECT * FROM isolation_rules
            WHERE profile_id = @profile
              AND host = @host COLLATE NOCASE
              AND port = @port
              AND database_name = @db COLLATE NOCASE
            ORDER BY priority DESC, id ASC;
            """,
            new
            {
                profile = profileId.Value,
                host = connection.Host,
                port = connection.Port ?? 0,
                db = connection.Database,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        var dynamicRules = rows.Select(MapRow).ToList();

        var staticRules = _statics.ForProfile(profileId)
            .Where(r => r.Scope.MatchesConnection(connection))
            .ToList();

        var merged = new List<IsolationRule>(dynamicRules.Count + staticRules.Count);
        merged.AddRange(staticRules);
        merged.AddRange(dynamicRules);
        merged.Sort(static (a, b) =>
        {
            var byPriority = b.Priority.CompareTo(a.Priority);
            return byPriority != 0
                ? byPriority
                : string.CompareOrdinal(a.Id.Value, b.Id.Value);
        });

        return merged;
    }

    /// <inheritdoc/>
    public async Task<IsolationRule?> GetAsync(IsolationRuleId id, CancellationToken cancellationToken)
    {
        var staticRule = _statics.Get(id);
        if (staticRule is not null)
        {
            return staticRule;
        }

        await using var sqlite = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var row = await sqlite.QuerySingleOrDefaultAsync<IsolationRuleRow>(new CommandDefinition(
            "SELECT * FROM isolation_rules WHERE id = @id LIMIT 1;",
            new { id = id.Value },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return row is null ? null : MapRow(row);
    }

    /// <inheritdoc/>
    public async Task<IsolationRule> UpsertAsync(IsolationRule rule, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (rule.Source == IsolationRuleSource.Static)
        {
            throw new InvalidOperationException(
                "Static isolation rules are immutable. Modify them via appsettings.json instead.");
        }

        if (_statics.IsStatic(rule.Id))
        {
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' is a static rule; refusing to overwrite it with a dynamic entry.");
        }

        await using var sqlite = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        await sqlite.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO isolation_rules(
                id, profile_id, host, port, database_name, schema_name, table_name,
                filter_kind, filter_payload_json, priority, description, created_at, updated_at)
            VALUES (
                @id, @profile, @host, @port, @db, @schema, @table,
                @kind, @payload, @priority, @description, @created, @updated)
            ON CONFLICT(id) DO UPDATE SET
                profile_id = excluded.profile_id,
                host = excluded.host,
                port = excluded.port,
                database_name = excluded.database_name,
                schema_name = excluded.schema_name,
                table_name = excluded.table_name,
                filter_kind = excluded.filter_kind,
                filter_payload_json = excluded.filter_payload_json,
                priority = excluded.priority,
                description = excluded.description,
                updated_at = excluded.updated_at;
            """,
            new
            {
                id = rule.Id.Value,
                profile = rule.ProfileId.Value,
                host = rule.Scope.Host,
                port = rule.Scope.Port,
                db = rule.Scope.DatabaseName,
                schema = rule.Scope.Schema,
                table = rule.Scope.Table,
                kind = rule.Filter.Kind,
                payload = SerializeFilter(rule.Filter),
                priority = rule.Priority,
                description = rule.Description,
                created = now,
                updated = now,
            },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return rule;
    }

    /// <inheritdoc/>
    public async Task<bool> DeleteAsync(IsolationRuleId id, CancellationToken cancellationToken)
    {
        if (_statics.IsStatic(id))
        {
            throw new InvalidOperationException(
                $"Rule '{id}' is static and cannot be deleted at runtime.");
        }

        await using var sqlite = await OpenAsync(cancellationToken).ConfigureAwait(false);
        var rows = await sqlite.ExecuteAsync(new CommandDefinition(
            "DELETE FROM isolation_rules WHERE id = @id;",
            new { id = id.Value },
            cancellationToken: cancellationToken)).ConfigureAwait(false);
        return rows > 0;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static string SerializeFilter(IsolationFilter filter)
    {
        return filter switch
        {
            IsolationFilter.EqualityFilter e => JsonSerializer.Serialize(new EqualityPayload
            {
                Column = e.Column,
                Value = ToJson(e.Value),
            }),
            IsolationFilter.InListFilter l => JsonSerializer.Serialize(new InListPayload
            {
                Column = l.Column,
                Values = l.Values.Select(ToJson).ToList(),
            }),
            IsolationFilter.RawSqlFilter r => JsonSerializer.Serialize(new RawSqlPayload
            {
                Predicate = r.Predicate,
                Parameters = r.Parameters.ToDictionary(
                    kv => kv.Key,
                    kv => ToJson(kv.Value),
                    StringComparer.Ordinal),
            }),
            _ => throw new InvalidOperationException(
                $"Unsupported filter kind '{filter.GetType().Name}'."),
        };
    }

    private static IsolationRule MapRow(IsolationRuleRow row)
    {
        var filter = DeserializeFilter(row.filter_kind, row.filter_payload_json);
        return new IsolationRule(
            new IsolationRuleId(row.id),
            new ProfileId(row.profile_id),
            new IsolationScope(row.host, row.port, row.database_name, row.schema_name, row.table_name),
            filter,
            IsolationRuleSource.Dynamic,
            row.priority,
            row.description);
    }

    private static IsolationFilter DeserializeFilter(string kind, string payload)
    {
        return kind switch
        {
            "Equality" => DeserializeEquality(payload),
            "InList" => DeserializeInList(payload),
            "RawSql" => DeserializeRawSql(payload),
            _ => throw new InvalidOperationException($"Unknown isolation filter kind '{kind}'."),
        };
    }

    private static IsolationFilter.EqualityFilter DeserializeEquality(string payload)
    {
        var data = JsonSerializer.Deserialize<EqualityPayload>(payload)
            ?? throw new InvalidOperationException("Empty Equality filter payload.");
        return new IsolationFilter.EqualityFilter(data.Column, FromJson(data.Value));
    }

    private static IsolationFilter.InListFilter DeserializeInList(string payload)
    {
        var data = JsonSerializer.Deserialize<InListPayload>(payload)
            ?? throw new InvalidOperationException("Empty InList filter payload.");
        return new IsolationFilter.InListFilter(
            data.Column,
            data.Values.Select(FromJson).ToList());
    }

    private static IsolationFilter.RawSqlFilter DeserializeRawSql(string payload)
    {
        var data = JsonSerializer.Deserialize<RawSqlPayload>(payload)
            ?? throw new InvalidOperationException("Empty RawSql filter payload.");
        return new IsolationFilter.RawSqlFilter(
            data.Predicate,
            data.Parameters.ToDictionary(
                kv => kv.Key,
                kv => FromJson(kv.Value),
                StringComparer.Ordinal));
    }

    private static JsonElement ToJson(object? value)
    {
        // Round-trip through JSON so the persisted payload uses a single
        // canonical representation (numbers, strings, booleans, null).
        var json = JsonSerializer.Serialize(value);
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    private static object? FromJson(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l)
                ? l
                : element.TryGetDouble(out var d) ? d : element.GetRawText(),
            JsonValueKind.Array => element.EnumerateArray().Select(FromJson).ToList(),
            JsonValueKind.Object => element.EnumerateObject()
                .ToDictionary(p => p.Name, p => FromJson(p.Value), StringComparer.Ordinal),
            _ => element.GetRawText(),
        };
    }

    private sealed class EqualityPayload
    {
        public string Column { get; set; } = string.Empty;
        public JsonElement Value { get; set; }
    }

    private sealed class InListPayload
    {
        public string Column { get; set; } = string.Empty;
        public List<JsonElement> Values { get; set; } = new();
    }

    private sealed class RawSqlPayload
    {
        public string Predicate { get; set; } = string.Empty;
        public Dictionary<string, JsonElement> Parameters { get; set; } = new(StringComparer.Ordinal);
    }

#pragma warning disable IDE1006, SA1300, CA1812 // Dapper maps snake_case columns onto these properties via reflection.
    private sealed class IsolationRuleRow
    {
        public string id { get; set; } = string.Empty;
        public string profile_id { get; set; } = ProfileId.DefaultValue;
        public string host { get; set; } = string.Empty;
        public int port { get; set; }
        public string database_name { get; set; } = string.Empty;
        public string schema_name { get; set; } = string.Empty;
        public string table_name { get; set; } = string.Empty;
        public string filter_kind { get; set; } = string.Empty;
        public string filter_payload_json { get; set; } = string.Empty;
        public int priority { get; set; }
        public string? description { get; set; }
        public string created_at { get; set; } = string.Empty;
        public string updated_at { get; set; } = string.Empty;
    }
#pragma warning restore IDE1006, SA1300, CA1812
}
