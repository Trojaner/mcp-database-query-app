using System.ComponentModel;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Server.Elicitation;
using McpDatabaseQueryApp.Server.Pagination;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class NoteTools
{
    private readonly INoteStore _notes;
    private readonly IElicitationGateway _elicitation;
    private readonly MutationGuard _mutationGuard;
    private readonly ILogger<NoteTools> _logger;

    public NoteTools(INoteStore notes, IElicitationGateway elicitation, MutationGuard mutationGuard, ILogger<NoteTools> logger)
    {
        _notes = notes;
        _elicitation = elicitation;
        _mutationGuard = mutationGuard;
        _logger = logger;
    }

    [McpServerTool(Name = "notes_set")]
    [Description("Attaches or updates a note on a database object (database, schema, table, column, or connection).")]
    public async Task<NoteRecord> SetAsync(NoteSetArgs args, CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.TargetPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(args.NoteText);

        var targetType = Enum.Parse<NoteTargetType>(args.TargetType, ignoreCase: true);
        var existing = await _notes.GetAsync(targetType, args.TargetPath, cancellationToken).ConfigureAwait(false);

        var note = new NoteRecord
        {
            Id = existing?.Id ?? ConnectionIdFactory.NewNoteId(),
            TargetType = targetType,
            TargetPath = args.TargetPath,
            NoteText = args.NoteText,
            CreatedAt = existing?.CreatedAt ?? DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        return await _notes.UpsertAsync(note, cancellationToken).ConfigureAwait(false);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "notes_get", ReadOnly = true)]
    [Description("Gets the note attached to a specific database object.")]
    public async Task<NoteRecord?> GetAsync(
        [Description("Target type: Database, Schema, Table, Column, or Connection.")] string targetType,
        [Description("Hierarchical path, e.g. 'mydb/public/users/email'.")] string targetPath,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var type = Enum.Parse<NoteTargetType>(targetType, ignoreCase: true);
        return await _notes.GetAsync(type, targetPath, cancellationToken).ConfigureAwait(false);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "notes_list", ReadOnly = true)]
    [Description("Lists notes, optionally filtered by target type and/or path prefix.")]
    public async Task<NotePageResult> ListAsync(
        [Description("Optional target type filter: Database, Schema, Table, Column, or Connection.")] string? targetType,
        [Description("Optional path prefix filter.")] string? pathPrefix,
        [Description("Pagination cursor.")] string? cursor,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var page = PageCodec.Decode(cursor, defaultLimit: 50);
        var type = targetType is null ? null : (NoteTargetType?)Enum.Parse<NoteTargetType>(targetType, ignoreCase: true);
        var (items, total) = await _notes.ListAsync(type, pathPrefix, page.Offset, page.Limit, cancellationToken).ConfigureAwait(false);
        var next = PageCodec.EncodeNext(page, items.Count, total);
        return new NotePageResult(items, total, next);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "notes_delete", Destructive = true)]
    [Description("Deletes a note from a database object.")]
    public async Task<NoteDeleteResult> DeleteAsync(
        McpServer server,
        [Description("Target type: Database, Schema, Table, Column, or Connection.")] string targetType,
        [Description("Hierarchical path of the target.")] string targetPath,
        [Description("Skip confirmation. Only effective with --dangerously-skip-permissions.")] bool confirm,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var type = Enum.Parse<NoteTargetType>(targetType, ignoreCase: true);

        if (!_mutationGuard.ShouldSkipElicitation(confirm))
        {
            var ok = await _elicitation.ConfirmAsync(server, $"Delete note on {targetType} '{targetPath}'?", cancellationToken).ConfigureAwait(false);
            if (!ok)
            {
                return new NoteDeleteResult(targetType, targetPath, Deleted: false);
            }
        }

        var deleted = await _notes.DeleteAsync(type, targetPath, cancellationToken).ConfigureAwait(false);
        return new NoteDeleteResult(targetType, targetPath, deleted);
        }, _logger).ConfigureAwait(false);
    }
}

public sealed class NoteSetArgs
{
    [Description("Target type: Database, Schema, Table, Column, or Connection.")]
    public required string TargetType { get; set; }

    [Description("Hierarchical path, e.g. 'mydb/public/users' for a table or 'mydb/public/users/email' for a column.")]
    public required string TargetPath { get; set; }

    [Description("The note text to attach.")]
    public required string NoteText { get; set; }
}

public sealed record NotePageResult(IReadOnlyList<NoteRecord> Items, long Total, string? NextCursor);

public sealed record NoteDeleteResult(string TargetType, string TargetPath, bool Deleted);
