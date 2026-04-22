using McpDatabaseQueryApp.Core.Connections;

namespace McpDatabaseQueryApp.Core.Providers;

public interface IDatabaseProvider
{
    DatabaseKind Kind { get; }

    ProviderCapabilities Capabilities { get; }

    string BuildConnectionString(ConnectionDescriptor descriptor, string password);

    Task<IDatabaseConnection> OpenAsync(ConnectionDescriptor descriptor, string password, CancellationToken cancellationToken, string? preassignedConnectionId = null);
}

public interface IDatabaseConnection : IAsyncDisposable
{
    string Id { get; }

    DatabaseKind Kind { get; }

    ConnectionDescriptor Descriptor { get; }

    bool IsReadOnly { get; }

    Task<QueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken);

    Task<long> ExecuteNonQueryAsync(NonQueryRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(CancellationToken cancellationToken);

    Task<(IReadOnlyList<TableInfo> Items, long Total)> ListTablesAsync(string? schema, PageRequest page, CancellationToken cancellationToken);

    Task<TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken);

    Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken);

    Task<ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken);

    Task PingAsync(CancellationToken cancellationToken);
}
