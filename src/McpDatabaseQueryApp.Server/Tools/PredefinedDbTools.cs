using System.ComponentModel;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Server.Elicitation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class PredefinedDbTools
{
    private readonly IMetadataStore _metadata;
    private readonly ICredentialProtector _protector;
    private readonly IElicitationGateway _elicitation;
    private readonly MutationGuard _mutationGuard;
    private readonly ILogger<PredefinedDbTools> _logger;

    public PredefinedDbTools(
        IMetadataStore metadata,
        ICredentialProtector protector,
        IElicitationGateway elicitation,
        MutationGuard mutationGuard,
        ILogger<PredefinedDbTools> logger)
    {
        _metadata = metadata;
        _protector = protector;
        _elicitation = elicitation;
        _mutationGuard = mutationGuard;
        _logger = logger;
    }

    [McpServerTool(Name = "db_predefined_get", ReadOnly = true)]
    [Description("Fetches redacted metadata for a pre-defined database.")]
    public async Task<RedactedDescriptor> GetAsync(string nameOrId, CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        // A bare null told the caller nothing about why the lookup came back
        // empty. Every other pre-defined lookup (db_connect included) reports a
        // miss as an error, so this one does too.
        var record = await _metadata.GetDatabaseAsync(nameOrId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pre-defined database '{nameOrId}' not found.");
        return RedactedDescriptor.From(record.Descriptor);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_predefined_create")]
    [Description("Registers a new pre-defined database. The password is stored encrypted; it never leaves the server in any response. Registering an entry that is not read-only asks for confirmation first.")]
    public async Task<RedactedDescriptor> CreateAsync(
        RequestContext<CallToolRequestParams> context,
        PredefinedDbArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        if (string.IsNullOrWhiteSpace(args.Password))
        {
            throw new InvalidOperationException("Password is required to create a pre-defined database.");
        }

        var descriptor = Build(args);

        if (!descriptor.ReadOnly)
        {
            await WriteAccessConfirmation.EnsureApprovedAsync(
                _elicitation,
                _mutationGuard,
                context,
                WriteAccessConfirmation.CreateInputKey,
                $"Register pre-defined database '{descriptor.Name}' ({descriptor.Provider} {Describe(descriptor)}) as WRITE-ENABLED (not read-only)? "
                + "Every session that connects by this name will be allowed to run writes and DDL.",
                args.Confirm,
                cancellationToken).ConfigureAwait(false);
        }

        var (cipher, nonce) = _protector.Encrypt(args.Password);
        await _metadata.UpsertDatabaseAsync(descriptor, cipher, nonce, cancellationToken).ConfigureAwait(false);
        return RedactedDescriptor.From(descriptor);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_predefined_update")]
    [Description("Updates metadata of an existing pre-defined database. Credentials are never rotated through this tool. Turning read-only off asks for confirmation first.")]
    public async Task<RedactedDescriptor> UpdateAsync(
        RequestContext<CallToolRequestParams> context,
        PredefinedDbUpdateArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        var existing = await _metadata.GetDatabaseAsync(args.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Pre-defined database '{args.Name}' not found.");
        var descriptor = new ConnectionDescriptor
        {
            Id = existing.Descriptor.Id,
            Name = args.Name,
            Provider = Enum.Parse<DatabaseKind>(args.Provider, ignoreCase: true),
            Host = args.Host,
            Port = args.Port,
            Database = args.Database,
            Username = args.Username,
            SslMode = args.SslMode ?? existing.Descriptor.SslMode,
            TrustServerCertificate = args.TrustServerCertificate ?? existing.Descriptor.TrustServerCertificate,
            ReadOnly = args.ReadOnly ?? existing.Descriptor.ReadOnly,
            DefaultSchema = args.DefaultSchema ?? existing.Descriptor.DefaultSchema,
            Tags = args.Tags ?? existing.Descriptor.Tags,
            CreatedAt = existing.Descriptor.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        // Only the transition matters: an entry that was already write-enabled
        // was confirmed when it got that way, so re-saving it does not re-ask.
        if (existing.Descriptor.ReadOnly && !descriptor.ReadOnly)
        {
            await WriteAccessConfirmation.EnsureApprovedAsync(
                _elicitation,
                _mutationGuard,
                context,
                WriteAccessConfirmation.UpdateInputKey,
                $"Turn read-only OFF for pre-defined database '{descriptor.Name}' ({descriptor.Provider} {Describe(descriptor)})? "
                + "Every session that connects by this name will be allowed to run writes and DDL.",
                args.Confirm,
                cancellationToken).ConfigureAwait(false);
        }

        await _metadata.UpdateDatabaseMetadataAsync(descriptor, cancellationToken).ConfigureAwait(false);
        return RedactedDescriptor.From(descriptor);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_predefined_delete", Destructive = true)]
    [Description("Deletes a pre-defined database entry. Elicits a confirmation from the user.")]
    public async Task<DeleteResult> DeleteAsync(
        RequestContext<CallToolRequestParams> context,
        [Description("Name or id of the pre-defined database to delete.")] string nameOrId,
        [Description("Skip the server-side confirmation elicitation. The MCP client may still show its own destructive-tool prompt via the tool's Destructive annotation.")] bool confirm,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var existing = await _metadata.GetDatabaseAsync(nameOrId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new DeleteResult(nameOrId, Deleted: false);
        }

        if (!confirm && _elicitation.CanElicit(context))
        {
            var ok = await _elicitation.ConfirmAsync(context, "confirm_delete_predefined", $"Delete pre-defined database '{existing.Descriptor.Name}'?", cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return new DeleteResult(nameOrId, Deleted: false);
            }
        }

        var deleted = await _metadata.DeleteDatabaseAsync(nameOrId, cancellationToken).ConfigureAwait(false);
        return new DeleteResult(nameOrId, deleted);
        }, _logger).ConfigureAwait(false);
    }

    private static ConnectionDescriptor Build(PredefinedDbArgs args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(args.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.Provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.Database);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.Username);

        return new ConnectionDescriptor
        {
            Id = ConnectionIdFactory.NewDatabaseId(),
            Name = args.Name,
            Provider = Enum.Parse<DatabaseKind>(args.Provider, ignoreCase: true),
            Host = args.Host,
            Port = args.Port,
            Database = args.Database,
            Username = args.Username,
            SslMode = args.SslMode ?? "Require",
            TrustServerCertificate = args.TrustServerCertificate ?? false,
            ReadOnly = args.ReadOnly ?? true,
            DefaultSchema = args.DefaultSchema,
            Tags = args.Tags ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>
    /// Renders the target of an entry for a confirmation prompt. Never includes
    /// credentials — only the coordinates the user needs to recognise which
    /// database is about to become writable.
    /// </summary>
    private static string Describe(ConnectionDescriptor descriptor) =>
        descriptor.Port is { } port
            ? $"{descriptor.Host}:{port}/{descriptor.Database} as {descriptor.Username}"
            : $"{descriptor.Host}/{descriptor.Database} as {descriptor.Username}";
}

public sealed class PredefinedDbArgs
{
    public required string Name { get; set; }

    public required string Provider { get; set; }

    public required string Host { get; set; }

    public int? Port { get; set; }

    public required string Database { get; set; }

    public required string Username { get; set; }

    [Description("Password is encrypted and never returned in any response.")]
    public string? Password { get; set; }

    public string? SslMode { get; set; }

    public bool? TrustServerCertificate { get; set; }

    [Description("Defaults to true. Setting it to false registers a write-enabled entry and requires an explicit confirmation.")]
    public bool? ReadOnly { get; set; }

    public string? DefaultSchema { get; set; }

    public IReadOnlyList<string>? Tags { get; set; }

    [Description("Skip the write-access confirmation. Only effective with --dangerously-skip-permissions.")]
    public bool Confirm { get; set; }
}

public sealed record DeleteResult(string NameOrId, bool Deleted);

public sealed class PredefinedDbUpdateArgs
{
    public required string Name { get; set; }

    public required string Provider { get; set; }

    public required string Host { get; set; }

    public int? Port { get; set; }

    public required string Database { get; set; }

    public required string Username { get; set; }

    public string? SslMode { get; set; }

    public bool? TrustServerCertificate { get; set; }

    [Description("Omit to keep the current setting. Changing it from true to false requires an explicit confirmation.")]
    public bool? ReadOnly { get; set; }

    public string? DefaultSchema { get; set; }

    public IReadOnlyList<string>? Tags { get; set; }

    [Description("Skip the write-access confirmation. Only effective with --dangerously-skip-permissions.")]
    public bool Confirm { get; set; }
}
