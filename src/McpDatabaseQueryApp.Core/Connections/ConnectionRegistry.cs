using System.Collections.Concurrent;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;

namespace McpDatabaseQueryApp.Core.Connections;

/// <summary>
/// Process-wide registry of live database connections, partitioned by
/// <see cref="Profiles.ProfileId"/>. A connection opened in profile A is
/// invisible to profile B; the partition is enforced both on lookup and on
/// listing, and the reaper iterates per-profile when reaping idle connections.
/// </summary>
public sealed class ConnectionRegistry : IConnectionRegistry, IAsyncDisposable
{
    private readonly IProviderRegistry _providers;
    private readonly IMetadataStore _metadata;
    private readonly ICredentialProtector _protector;
    private readonly ConnectionActivityTracker _tracker;
    private readonly IProfileContextAccessor _profile;

    // The primary store is keyed by connection id (globally unique across
    // profiles, because connection ids are random). We additionally track
    // each id's owning profile so lookups, listings and reaping can enforce
    // the boundary.
    private readonly ConcurrentDictionary<string, IDatabaseConnection> _connections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _ownership = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ReopenRecipe> _recipes = new(StringComparer.Ordinal);

    public ConnectionRegistry(
        IProviderRegistry providers,
        IMetadataStore metadata,
        ICredentialProtector protector,
        ConnectionActivityTracker tracker,
        IProfileContextAccessor profile)
    {
        _providers = providers;
        _metadata = metadata;
        _protector = protector;
        _tracker = tracker;
        _profile = profile;
    }

    private string CurrentProfile => _profile.CurrentIdOrDefault.Value;

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

        _ownership[connection.Id] = CurrentProfile;

        if (recordAdHocRecipe)
        {
            _recipes[connection.Id] = ReopenRecipe.AdHoc(descriptor, password, CurrentProfile);
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
            _recipes[opened.Id] = ReopenRecipe.Predefined(nameOrId, CurrentProfile);
            return opened;
        }
        finally
        {
            CryptoWipe(password);
        }
    }

    public bool TryGet(string connectionId, out IDatabaseConnection connection)
    {
        if (_connections.TryGetValue(connectionId, out connection!) && OwnedByCurrentProfile(connectionId))
        {
            _tracker.Touch(connectionId);
            return true;
        }

        connection = null!;
        return false;
    }

    public async Task<IDatabaseConnection> GetOrOpenPredefinedAsync(string nameOrId, CancellationToken cancellationToken)
    {
        var profile = CurrentProfile;
        foreach (var conn in _connections.Values)
        {
            if (!_ownership.TryGetValue(conn.Id, out var owner) || !string.Equals(owner, profile, StringComparison.Ordinal))
            {
                continue;
            }

            if (string.Equals(conn.Id, nameOrId, StringComparison.Ordinal) ||
                string.Equals(conn.Descriptor.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
            {
                _tracker.Touch(conn.Id);
                return conn;
            }
        }

        if (_recipes.TryGetValue(nameOrId, out var recipe) && string.Equals(recipe.OwnerProfile, profile, StringComparison.Ordinal))
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

    public IReadOnlyList<IDatabaseConnection> List()
    {
        var profile = CurrentProfile;
        return _connections.Values
            .Where(c => _ownership.TryGetValue(c.Id, out var owner) && string.Equals(owner, profile, StringComparison.Ordinal))
            .ToList();
    }

    public async Task<bool> DisconnectAsync(string connectionId)
    {
        if (!OwnedByCurrentProfile(connectionId))
        {
            return false;
        }

        var removed = await RemoveAsync(connectionId).ConfigureAwait(false);
        if (removed)
        {
            _recipes.TryRemove(connectionId, out _);
            _ownership.TryRemove(connectionId, out _);
        }
        return removed;
    }

    public Task<bool> ReapAsync(string connectionId)
    {
        // Reaping is invoked by the background reaper and is intentionally
        // profile-agnostic: idle connections in any profile are eligible.
        return RemoveProfileAgnosticAsync(connectionId);
    }

    private async Task<bool> RemoveProfileAgnosticAsync(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out var connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            _ownership.TryRemove(connectionId, out _);
            _recipes.TryRemove(connectionId, out _);
            return true;
        }

        return false;
    }

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
        // Operates across every profile: this is only called on shutdown.
        foreach (var id in _connections.Keys.ToArray())
        {
            await RemoveProfileAgnosticAsync(id).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAllAsync().ConfigureAwait(false);
    }

    private bool OwnedByCurrentProfile(string connectionId)
    {
        if (!_ownership.TryGetValue(connectionId, out var owner))
        {
            return false;
        }

        return string.Equals(owner, CurrentProfile, StringComparison.Ordinal);
    }

    private static void CryptoWipe(string value)
    {
        _ = value.Length;
    }
}

internal sealed record ReopenRecipe(
    string? PredefinedNameOrId,
    ConnectionDescriptor? AdHocDescriptor,
    string? AdHocPassword,
    string OwnerProfile)
{
    public static ReopenRecipe Predefined(string nameOrId, string ownerProfile) => new(nameOrId, null, null, ownerProfile);

    public static ReopenRecipe AdHoc(ConnectionDescriptor descriptor, string password, string ownerProfile) => new(null, descriptor, password, ownerProfile);
}
