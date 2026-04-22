using System.ComponentModel;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Server.Elicitation;
using McpDatabaseQueryApp.Server.Pagination;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class ScriptTools
{
    private readonly IScriptStore _scripts;
    private readonly IConnectionRegistry _registry;
    private readonly IElicitationGateway _elicitation;
    private readonly MutationGuard _mutationGuard;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly ILogger<ScriptTools> _logger;

    public ScriptTools(
        IScriptStore scripts,
        IConnectionRegistry registry,
        IElicitationGateway elicitation,
        MutationGuard mutationGuard,
        McpDatabaseQueryAppOptions options,
        ILogger<ScriptTools> logger)
    {
        _scripts = scripts;
        _registry = registry;
        _elicitation = elicitation;
        _mutationGuard = mutationGuard;
        _options = options;
        _logger = logger;
    }

    [McpServerTool(Name = "scripts_list", ReadOnly = true)]
    [Description("Lists saved SQL scripts.")]
    public async Task<ScriptPageResult> ListAsync(
        [Description("Pagination cursor.")] string? cursor,
        [Description("Optional substring filter matching name or description.")] string? filter,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var page = PageCodec.Decode(cursor, defaultLimit: 50);
        var (items, total) = await _scripts.ListAsync(page.Offset, page.Limit, filter, cancellationToken).ConfigureAwait(false);
        var next = PageCodec.EncodeNext(page, items.Count, total);
        return new ScriptPageResult(items, total, next);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "scripts_get", ReadOnly = true)]
    [Description("Fetches a saved SQL script.")]
    public async Task<ScriptRecord?> GetAsync(string nameOrId, CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        return await _scripts.GetAsync(nameOrId, cancellationToken).ConfigureAwait(false);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "scripts_create")]
    [Description("Creates a new saved SQL script. Rejects if the name already exists.")]
    public async Task<ScriptRecord> CreateAsync(McpServer server, ScriptArgs args, CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        var existing = await _scripts.GetAsync(args.Name, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            throw new InvalidOperationException($"Script '{args.Name}' already exists. Use scripts_update.");
        }

        var record = Build(args);
        var result = await _scripts.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
        await server.SendNotificationAsync(NotificationMethods.PromptListChangedNotification, new PromptListChangedNotificationParams()).ConfigureAwait(false);
        return result;
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "scripts_update")]
    [Description("Updates an existing saved SQL script.")]
    public async Task<ScriptRecord> UpdateAsync(
        McpServer server,
        ScriptArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        var existing = await _scripts.GetAsync(args.Name, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Script '{args.Name}' not found.");
        var updated = Build(args) with { Id = existing.Id, CreatedAt = existing.CreatedAt, UpdatedAt = DateTimeOffset.UtcNow };
        var result = await _scripts.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        await server.SendNotificationAsync(NotificationMethods.PromptListChangedNotification, new PromptListChangedNotificationParams()).ConfigureAwait(false);
        return result;
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "scripts_delete", Destructive = true)]
    [Description("Deletes a saved SQL script. Elicits a confirmation unless confirm=true.")]
    public async Task<DeleteResult> DeleteAsync(
        McpServer server,
        string nameOrId,
        [Description("Skip confirmation. Only effective with --dangerously-skip-permissions.")] bool confirm,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var existing = await _scripts.GetAsync(nameOrId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return new DeleteResult(nameOrId, Deleted: false);
        }

        if (!_mutationGuard.ShouldSkipElicitation(confirm))
        {
            var ok = await _elicitation.ConfirmAsync(server, $"Delete script '{existing.Name}'?", cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return new DeleteResult(nameOrId, Deleted: false);
            }
        }

        var deleted = await _scripts.DeleteAsync(nameOrId, cancellationToken).ConfigureAwait(false);
        if (deleted)
        {
            await server.SendNotificationAsync(NotificationMethods.PromptListChangedNotification, new PromptListChangedNotificationParams()).ConfigureAwait(false);
        }

        return new DeleteResult(nameOrId, deleted);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "scripts_run", Destructive = true)]
    [Description("Executes a saved SQL script against a connection. Destructive scripts require confirmation.")]
    public async Task<ScriptRunResult> RunAsync(
        McpServer server,
        string nameOrId,
        string connectionId,
        [Description("Skip confirmation. Only effective with --dangerously-skip-permissions.")] bool confirm,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var script = await _scripts.GetAsync(nameOrId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Script '{nameOrId}' not found.");

        if (!_registry.TryGet(connectionId, out var connection))
        {
            if (_options.AutoConnect)
            {
                connection = await _registry.GetOrOpenPredefinedAsync(connectionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new KeyNotFoundException($"Connection '{connectionId}' not found.");
            }
        }

        if (script.Provider is { } requiredProvider && requiredProvider != connection.Kind)
        {
            throw new InvalidOperationException($"Script '{script.Name}' targets {requiredProvider} but connection is {connection.Kind}.");
        }

        var destructive = script.Destructive || ScriptSafetyAnalyzer.IsLikelyDestructive(script.SqlText);
        if (destructive && !_mutationGuard.ShouldSkipElicitation(confirm))
        {
            var message = $"The following SQL will be executed:\n\n{script.SqlText}\n\nProceed?";
            var ok = await _elicitation.ConfirmAsync(server, message, cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return new ScriptRunResult(script.Name, connectionId, Executed: false, RowsAffected: 0, RowCount: 0);
            }
        }

        if (ScriptSafetyAnalyzer.IsReadOnly(script.SqlText))
        {
            var query = await connection.ExecuteQueryAsync(
                new QueryRequest(script.SqlText, null, null, null),
                cancellationToken).ConfigureAwait(false);
            return new ScriptRunResult(script.Name, connectionId, Executed: true, RowsAffected: 0, RowCount: query.RowCount);
        }

        if (connection.IsReadOnly)
        {
            throw new InvalidOperationException("Connection is read-only. Script contains writes.");
        }

        var affected = await connection.ExecuteNonQueryAsync(
            new NonQueryRequest(script.SqlText, null, null),
            cancellationToken).ConfigureAwait(false);
        return new ScriptRunResult(script.Name, connectionId, Executed: true, RowsAffected: affected, RowCount: 0);
        }, _logger).ConfigureAwait(false);
    }

    private static ScriptRecord Build(ScriptArgs args)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(args.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.SqlText);
        return new ScriptRecord
        {
            Id = ConnectionIdFactory.NewScriptId(),
            Name = args.Name,
            Description = args.Description,
            Provider = args.Provider is null ? null : Enum.Parse<DatabaseKind>(args.Provider, ignoreCase: true),
            SqlText = args.SqlText,
            Destructive = args.Destructive ?? ScriptSafetyAnalyzer.IsLikelyDestructive(args.SqlText),
            Tags = args.Tags ?? [],
            Notes = args.Notes,
            Parameters = args.Parameters ?? [],
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }
}

public sealed class ScriptArgs
{
    public required string Name { get; set; }

    public string? Description { get; set; }

    public string? Provider { get; set; }

    public required string SqlText { get; set; }

    public bool? Destructive { get; set; }

    public IReadOnlyList<string>? Tags { get; set; }

    [Description("Free-form notes about this script.")]
    public string? Notes { get; set; }

    [Description("Named parameters for the script SQL. Each entry specifies name, optional description, default value, and whether it is required.")]
    public IReadOnlyList<ScriptParameter>? Parameters { get; set; }
}

public sealed record ScriptPageResult(IReadOnlyList<ScriptRecord> Items, long Total, string? NextCursor);

public sealed record ScriptRunResult(string ScriptName, string ConnectionId, bool Executed, long RowsAffected, int RowCount);
