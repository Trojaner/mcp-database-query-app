using System.ComponentModel;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Server.Elicitation;
using McpDatabaseQueryApp.Server.Metadata;
using McpDatabaseQueryApp.Server.Pagination;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class ConnectionTools
{
    private readonly IConnectionRegistry _registry;
    private readonly IProviderRegistry _providers;
    private readonly IMetadataStore _metadata;
    private readonly IElicitationGateway _elicitation;
    private readonly MetadataCache _metadataCache;
    private readonly ILogger<ConnectionTools> _logger;

    public ConnectionTools(
        IConnectionRegistry registry,
        IProviderRegistry providers,
        IMetadataStore metadata,
        IElicitationGateway elicitation,
        MetadataCache metadataCache,
        ILogger<ConnectionTools> logger)
    {
        _registry = registry;
        _providers = providers;
        _metadata = metadata;
        _elicitation = elicitation;
        _metadataCache = metadataCache;
        _logger = logger;
    }

    [McpServerTool(Name = "db_list_predefined", ReadOnly = true)]
    [Description("Lists configured (pre-defined) databases. Credentials are never returned.")]
    public async Task<ListPredefinedResult> ListPredefinedAsync(
        [Description("Optional pagination cursor returned by a previous call.")] string? cursor,
        [Description("Optional name/database substring filter.")] string? filter,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var page = PageCodec.Decode(cursor, defaultLimit: 50);
        var (items, total) = await _metadata.ListDatabasesAsync(page.Offset, page.Limit, filter, cancellationToken).ConfigureAwait(false);
        var nextCursor = PageCodec.EncodeNext(page, items.Count, total);
        var active = _registry.List().Select(c => c.Descriptor.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var redacted = items.Select(RedactedDescriptor.From).ToList();
        return new ListPredefinedResult(redacted, total, nextCursor, active);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_list_connections", ReadOnly = true)]
    [Description("Lists currently open connections and their redacted metadata.")]
    public ListConnectionsResult ListConnections()
    {
        return ToolErrorHandler.Wrap(() =>
        {
        var items = _registry.List()
            .Select(conn => RedactedDescriptor.From(conn.Descriptor))
            .ToList();
        return new ListConnectionsResult(items, items.Count);
        }, _logger);
    }

    [McpServerTool(Name = "db_connect", Destructive = false)]
    [Description("Opens a database connection. Provide a pre-defined 'name' or an ad-hoc descriptor. Passwords must be set out-of-band via db_predefined_create.")]
    public async Task<ConnectResult> ConnectAsync(
        McpServer server,
        ConnectArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        if (!string.IsNullOrWhiteSpace(args.Name))
        {
            var connection = await _registry.OpenPredefinedAsync(args.Name, cancellationToken).ConfigureAwait(false);
            _ = _metadataCache.GetOrCreate(connection.Id);
            return new ConnectResult(connection.Id, RedactedDescriptor.From(connection.Descriptor));
        }

        if (args.Provider is null || args.Host is null || args.Database is null || args.Username is null)
        {
            throw new ArgumentException("Ad-hoc connection requires provider, host, database, and username.");
        }

        if (args.Password is null)
        {
            throw new InvalidOperationException(
                "Password was not supplied. Store the connection via db_predefined_create (URL-mode elicitation) and retry using the predefined name.");
        }

        if (!_providers.TryGet(args.Provider, out _))
        {
            throw new InvalidOperationException($"Unknown provider '{args.Provider}'.");
        }

        var descriptor = new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = args.DisplayName ?? $"{args.Provider}:{args.Host}/{args.Database}",
            Provider = Enum.Parse<DatabaseKind>(args.Provider, ignoreCase: true),
            Host = args.Host,
            Port = args.Port,
            Database = args.Database,
            Username = args.Username,
            SslMode = args.SslMode ?? "None",
            TrustServerCertificate = args.TrustServerCertificate ?? false,
            ReadOnly = args.ReadOnly ?? true,
            DefaultSchema = args.DefaultSchema,
            Tags = args.Tags ?? [],
        };

        var opened = await _registry.OpenAsync(descriptor, args.Password, cancellationToken).ConfigureAwait(false);
        _ = _metadataCache.GetOrCreate(opened.Id);
        return new ConnectResult(opened.Id, RedactedDescriptor.From(opened.Descriptor));
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_disconnect")]
    [Description("Closes an open connection by id.")]
    public async Task<DisconnectResult> DisconnectAsync(
        [Description("Opaque connection id returned from db_connect.")] string connectionId,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        _ = cancellationToken;
        var removed = await _registry.DisconnectAsync(connectionId).ConfigureAwait(false);
        if (removed)
        {
            _metadataCache.Forget(connectionId);
        }

        return new DisconnectResult(connectionId, removed);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_ping", ReadOnly = true)]
    [Description("Runs SELECT 1 against an open connection.")]
    public async Task<PingResult> PingAsync(string connectionId, CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        if (!_registry.TryGet(connectionId, out var connection))
        {
            throw new KeyNotFoundException($"Connection '{connectionId}' not found.");
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await connection.PingAsync(cancellationToken).ConfigureAwait(false);
        sw.Stop();
        return new PingResult(connectionId, true, sw.ElapsedMilliseconds);
        }, _logger).ConfigureAwait(false);
    }
}

public sealed class ConnectArgs
{
    [Description("Pre-defined database name or id.")]
    public string? Name { get; set; }

    [Description("'Postgres' or 'SqlServer'. Required for ad-hoc connections.")]
    public string? Provider { get; set; }

    public string? Host { get; set; }

    public int? Port { get; set; }

    public string? Database { get; set; }

    public string? Username { get; set; }

    [Description("Password for ad-hoc use only. Prefer db_predefined_create for reusable connections.")]
    public string? Password { get; set; }

    [Description("Display name for ad-hoc connections.")]
    public string? DisplayName { get; set; }

    public string? SslMode { get; set; }

    public bool? TrustServerCertificate { get; set; }

    public bool? ReadOnly { get; set; }

    public string? DefaultSchema { get; set; }

    public IReadOnlyList<string>? Tags { get; set; }
}

public sealed record ConnectResult(string ConnectionId, RedactedDescriptor Descriptor);

public sealed record DisconnectResult(string ConnectionId, bool Closed);

public sealed record PingResult(string ConnectionId, bool Ok, long ElapsedMs);

public sealed record ListConnectionsResult(IReadOnlyList<RedactedDescriptor> Items, int Count);

public sealed record ListPredefinedResult(IReadOnlyList<RedactedDescriptor> Items, long Total, string? NextCursor, IReadOnlySet<string> ActiveNames);
