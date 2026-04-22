using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Core.Scripts;
using FluentAssertions;
using Xunit;
using ProvidersNs = McpDatabaseQueryApp.Core.Providers;
using ResultsNs = McpDatabaseQueryApp.Core.Results;

namespace McpDatabaseQueryApp.Core.Tests.Connections;

public sealed class ConnectionRegistryTests
{
    [Fact]
    public async Task Open_adds_connection_and_returns_id()
    {
        var provider = new FakeProvider();
        var registry = new ConnectionRegistry(new ProviderRegistry([provider]), new FakeMetadata(), new FakeProtector(), new ConnectionActivityTracker());

        var descriptor = SampleDescriptor();
        var conn = await registry.OpenAsync(descriptor, "pw", CancellationToken.None);

        conn.Id.Should().NotBeNullOrEmpty();
        registry.TryGet(conn.Id, out _).Should().BeTrue();
        registry.List().Should().ContainSingle();
    }

    [Fact]
    public async Task Disconnect_removes_connection()
    {
        var registry = new ConnectionRegistry(new ProviderRegistry([new FakeProvider()]), new FakeMetadata(), new FakeProtector(), new ConnectionActivityTracker());
        var conn = await registry.OpenAsync(SampleDescriptor(), "pw", CancellationToken.None);

        var removed = await registry.DisconnectAsync(conn.Id);

        removed.Should().BeTrue();
        registry.List().Should().BeEmpty();
    }

    [Fact]
    public async Task DisconnectAll_closes_every_connection()
    {
        var registry = new ConnectionRegistry(new ProviderRegistry([new FakeProvider()]), new FakeMetadata(), new FakeProtector(), new ConnectionActivityTracker());
        await registry.OpenAsync(SampleDescriptor() with { Name = "one" }, "pw", CancellationToken.None);
        await registry.OpenAsync(SampleDescriptor() with { Name = "two" }, "pw", CancellationToken.None);

        await registry.DisconnectAllAsync();

        registry.List().Should().BeEmpty();
    }

    [Fact]
    public async Task OpenPredefined_throws_for_unknown()
    {
        var registry = new ConnectionRegistry(new ProviderRegistry([new FakeProvider()]), new FakeMetadata(), new FakeProtector(), new ConnectionActivityTracker());
        Func<Task> act = async () => await registry.OpenPredefinedAsync("nope", CancellationToken.None);
        await act.Should().ThrowAsync<KeyNotFoundException>();
    }

    private static ConnectionDescriptor SampleDescriptor() => new()
    {
        Id = ConnectionIdFactory.NewDatabaseId(),
        Name = "sample",
        Provider = DatabaseKind.Postgres,
        Host = "localhost",
        Database = "app",
        Username = "u",
        SslMode = "Require",
    };

    private sealed class FakeProvider : IDatabaseProvider
    {
        public DatabaseKind Kind => DatabaseKind.Postgres;

        public ProviderCapabilities Capabilities { get; } = new(true, true, false, false, true, false, [], []);

        public string BuildConnectionString(ConnectionDescriptor descriptor, string password) => "fake";

        public Task<IDatabaseConnection> OpenAsync(ConnectionDescriptor descriptor, string password, CancellationToken cancellationToken, string? preassignedConnectionId = null)
            => Task.FromResult<IDatabaseConnection>(new FakeConnection(preassignedConnectionId ?? ConnectionIdFactory.NewConnectionId(), descriptor));
    }

    private sealed class FakeConnection : IDatabaseConnection
    {
        public FakeConnection(string id, ConnectionDescriptor descriptor) { Id = id; Descriptor = descriptor; }
        public string Id { get; }
        public DatabaseKind Kind => DatabaseKind.Postgres;
        public ConnectionDescriptor Descriptor { get; }
        public bool IsReadOnly => Descriptor.ReadOnly;
        public Task PingAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<ProvidersNs.QueryResult> ExecuteQueryAsync(ProvidersNs.QueryRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<long> ExecuteNonQueryAsync(ProvidersNs.NonQueryRequest r, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProvidersNs.SchemaInfo>> ListSchemasAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<(IReadOnlyList<ProvidersNs.TableInfo> Items, long Total)> ListTablesAsync(string? schema, ProvidersNs.PageRequest page, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProvidersNs.TableDetails> DescribeTableAsync(string schema, string table, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProvidersNs.RoleInfo>> ListRolesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProvidersNs.DatabaseInfo>> ListDatabasesAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ProvidersNs.ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeProtector : ICredentialProtector
    {
        public (byte[] Cipher, byte[] Nonce) Encrypt(string plaintext) => (System.Text.Encoding.UTF8.GetBytes(plaintext), new byte[12]);
        public string Decrypt(byte[] cipher, byte[] nonce) => System.Text.Encoding.UTF8.GetString(cipher);
    }

    private sealed class FakeMetadata : IMetadataStore
    {
        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<DatabaseRecord?> GetDatabaseAsync(string nameOrId, CancellationToken cancellationToken) => Task.FromResult<DatabaseRecord?>(null);
        public Task<(IReadOnlyList<ConnectionDescriptor> Items, long Total)> ListDatabasesAsync(int offset, int limit, string? filter, CancellationToken cancellationToken) => Task.FromResult(((IReadOnlyList<ConnectionDescriptor>)Array.Empty<ConnectionDescriptor>(), 0L));
        public Task<ConnectionDescriptor> UpsertDatabaseAsync(ConnectionDescriptor descriptor, byte[] passwordCipher, byte[] passwordNonce, CancellationToken cancellationToken) => Task.FromResult(descriptor);
        public Task<ConnectionDescriptor> UpdateDatabaseMetadataAsync(ConnectionDescriptor descriptor, CancellationToken cancellationToken) => Task.FromResult(descriptor);
        public Task<bool> DeleteDatabaseAsync(string nameOrId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<ScriptRecord?> GetScriptAsync(string nameOrId, CancellationToken cancellationToken) => Task.FromResult<ScriptRecord?>(null);
        public Task<(IReadOnlyList<ScriptRecord> Items, long Total)> ListScriptsAsync(int offset, int limit, string? filter, CancellationToken cancellationToken) => Task.FromResult(((IReadOnlyList<ScriptRecord>)Array.Empty<ScriptRecord>(), 0L));
        public Task<ScriptRecord> UpsertScriptAsync(ScriptRecord script, CancellationToken cancellationToken) => Task.FromResult(script);
        public Task<bool> DeleteScriptAsync(string nameOrId, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<ResultsNs.ResultSetRecord?> GetResultSetAsync(string id, CancellationToken cancellationToken) => Task.FromResult<ResultsNs.ResultSetRecord?>(null);
        public Task InsertResultSetAsync(ResultsNs.ResultSetRecord record, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PurgeExpiredResultSetsAsync(DateTimeOffset now, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<NoteRecord?> GetNoteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken) => Task.FromResult<NoteRecord?>(null);
        public Task<(IReadOnlyList<NoteRecord> Items, long Total)> ListNotesAsync(NoteTargetType? targetType, string? pathPrefix, int offset, int limit, CancellationToken cancellationToken) => Task.FromResult(((IReadOnlyList<NoteRecord>)Array.Empty<NoteRecord>(), 0L));
        public Task<NoteRecord> UpsertNoteAsync(NoteRecord note, CancellationToken cancellationToken) => Task.FromResult(note);
        public Task<bool> DeleteNoteAsync(NoteTargetType targetType, string targetPath, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task<IReadOnlyDictionary<string, NoteRecord>> GetNotesBulkAsync(NoteTargetType targetType, IReadOnlyList<string> targetPaths, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, NoteRecord>>(new Dictionary<string, NoteRecord>());
    }
}
