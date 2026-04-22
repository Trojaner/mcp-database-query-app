using McpDatabaseQueryApp.Core.Providers;

namespace McpDatabaseQueryApp.Core.Connections;

public interface IConnectionRegistry
{
    Task<IDatabaseConnection> OpenAsync(ConnectionDescriptor descriptor, string password, CancellationToken cancellationToken);

    Task<IDatabaseConnection> OpenPredefinedAsync(string nameOrId, CancellationToken cancellationToken);

    Task<IDatabaseConnection> GetOrOpenPredefinedAsync(string nameOrId, CancellationToken cancellationToken);

    bool TryGet(string connectionId, out IDatabaseConnection connection);

    IReadOnlyList<IDatabaseConnection> List();

    Task<bool> DisconnectAsync(string connectionId);

    Task<bool> ReapAsync(string connectionId);

    Task DisconnectAllAsync();
}
