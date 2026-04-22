namespace McpDatabaseQueryApp.Core.Scripts;

public sealed record ScriptRecord
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public string? Description { get; init; }

    public DatabaseKind? Provider { get; init; }

    public required string SqlText { get; init; }

    public bool Destructive { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Notes { get; init; }

    public IReadOnlyList<ScriptParameter> Parameters { get; init; } = [];

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
