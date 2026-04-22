using System.Collections.Concurrent;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;

namespace McpDatabaseQueryApp.Core.Connections;

public sealed class ConnectionRegistry : IConnectionRegistry, IAsyncDisposable
{
    private readonly IProviderRegistry _providers;
    private readonly IMetadataStore _metadata;
    private readonly ICredentialProtector _protector;
    private readonly ConnectionActivityTracker _tracker;
    private readonly ConcurrentDictionary<string, IDatabaseConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReopenRecipe> _recipes = new(StringComparer.Ordinal);

    public ConnectionRegistry(
        IProviderRegistry providers,
        IMetadataStore metadata,
        ICredentialProtector protector,
        ConnectionActivityTracker tracker)
    {
        _providers = providers;
        _metadata = metadata;
        _protector = protector;
        _tracker = tracker;
    }

    public Task<IDatabaseConnection> OpenAsync(
        ConnectionDescriptor descriptor,
        string password,
        CancellationToken cancellationToken)
        => OpenInternalAsync(descriptor, password, preassignedId: null, recordAdHocRecipe: true, cancellationToken);

    private async Task<IDatabaseConnection> OpenInternalAsync(
        ConnectionDescriptor descriptor,
        string password,
        string? preassignedId,
        bool recordAdHocRecipe,
        CancellationToken cancellationToken)
    {
        var provider = _providers.Get(descriptor.Provider);
        var connection = await provider.OpenAsync(descriptor, password, cancellationToken, preassignedId).ConfigureAwait(false);
        if (!_connections.TryAdd(connection.Id, connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidOperationException($"Connection id collision for {connection.Id}.");
        }

        if (recordAdHocRecipe)
        {
            _recipes[connection.Id] = ReopenRecipe.AdHoc(descriptor, password);
        }

        _tracker.Touch(connection.Id);
        return connection;
    }

    public async Task<IDatabaseConnection> OpenPredefinedAsync(string nameOrId, CancellationToken cancellationToken)
    {
        var record = await _metadata.GetDatabaseAsync(nameOrId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pre-defined database '{nameOrId}' not found.");

        string password;
        try
        {
            password = _protector.Decrypt(record.PasswordCipher, record.PasswordNonce);
        }
        catch (System.Security.Cryptography.AuthenticationTagMismatchException ex)
        {
            throw new InvalidOperationException(
                $"Cannot decrypt stored password for pre-defined database '{record.Descriptor.Name}'. " +
                "The master key (McpDatabaseQueryApp:Secrets:KeyRef) does not match the key used when this record was created. " +
                "Either restore the original key, or re-register this database with db_predefined_create.",
                ex);
        }
        try
        {
            var opened = await OpenInternalAsync(record.Descriptor, password, preassignedId: null, recordAdHocRecipe: false, cancellationToken).ConfigureAwait(false);
            _recipes[opened.Id] = ReopenRecipe.Predefined(nameOrId);
            return opened;
        }
        finally
        {
            CryptoWipe(password);
        }
    }

    public bool TryGet(string connectionId, out IDatabaseConnection connection)
    {
        if (_connections.TryGetValue(connectionId, out connection!))
        {
            _tracker.Touch(connectionId);
            return true;
        }

        return false;
    }

    public async Task<IDatabaseConnection> GetOrOpenPredefinedAsync(string nameOrId, CancellationToken cancellationToken)
    {
        foreach (var conn in _connections.Values)
        {
            if (string.Equals(conn.Id, nameOrId, StringComparison.Ordinal) ||
                string.Equals(conn.Descriptor.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            {
                _tracker.Touch(conn.Id);
                return conn;
            }
        }

        if (_recipes.TryGetValue(nameOrId, out var recipe))
        {
            return await ReplayRecipeAsync(nameOrId, recipe, cancellationToken).ConfigureAwait(false);
        }

        return await OpenPredefinedAsync(nameOrId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IDatabaseConnection> ReplayRecipeAsync(string preservedId, ReopenRecipe recipe, CancellationToken cancellationToken)
    {
        if (recipe.PredefinedNameOrId is { } name)
        {
            var record = await _metadata.GetDatabaseAsync(name, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Pre-defined database '{name}' not found.");
            string password;
            try
            {
                password = _protector.Decrypt(record.PasswordCipher, record.PasswordNonce);
            }
            catch (System.Security.Cryptography.AuthenticationTagMismatchException ex)
            {
                throw new InvalidOperationException(
                    $"Cannot decrypt stored password for pre-defined database '{record.Descriptor.Name}'. " +
                    "The master key (McpDatabaseQueryApp:Secrets:KeyRef) does not match the key used when this record was created. " +
                    "Either restore the original key, or re-register this database with db_predefined_create.",
                    ex);
            }
            try
            {
                return await OpenInternalAsync(record.Descriptor, password, preservedId, recordAdHocRecipe: false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                CryptoWipe(password);
            }
        }

        if (recipe.AdHocDescriptor is { } descriptor && recipe.AdHocPassword is { } pwd)
        {
            return await OpenInternalAsync(descriptor, pwd, preservedId, recordAdHocRecipe: false, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException($"Reopen recipe for '{preservedId}' is empty.");
    }

    public IReadOnlyList<IDatabaseConnection> List() => [.. _connections.Values];

    public async Task<bool> DisconnectAsync(string connectionId)
    {
        var removed = await RemoveAsync(connectionId).ConfigureAwait(false);
        if (removed)
        {
            _recipes.TryRemove(connectionId, out _);
        }
        return removed;
    }

    public Task<bool> ReapAsync(string connectionId) => RemoveAsync(connectionId);

    private async Task<bool> RemoveAsync(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return true;
        }

        return false;
    }

    public async Task DisconnectAllAsync()
    {
        foreach (var id in _connections.Keys.ToArray())
        {
            await DisconnectAsync(id).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAllAsync().ConfigureAwait(false);
    }

    private static void CryptoWipe(string value)
    {
        _ = value.Length;
    }
}

internal sealed record ReopenRecipe(
    string? PredefinedNameOrId,
    ConnectionDescriptor? AdHocDescriptor,
    string? AdHocPassword)
{
    public static ReopenRecipe Predefined(string nameOrId) => new(nameOrId, null, null);

    public static ReopenRecipe AdHoc(ConnectionDescriptor descriptor, string password) => new(null, descriptor, password);
}
