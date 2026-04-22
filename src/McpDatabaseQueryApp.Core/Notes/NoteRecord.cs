namespace McpDatabaseQueryApp.Core.Notes;

public sealed record NoteRecord
{
    public required string Id { get; init; }

    public required NoteTargetType TargetType { get; init; }

    public required string TargetPath { get; init; }

    public required string NoteText { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
