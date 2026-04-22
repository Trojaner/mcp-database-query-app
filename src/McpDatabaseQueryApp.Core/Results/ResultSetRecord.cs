using McpDatabaseQueryApp.Core.Providers;

namespace McpDatabaseQueryApp.Core.Results;

public sealed record ResultSetRecord
{
    public required string Id { get; init; }

    public required string ConnectionId { get; init; }

    public required IReadOnlyList<QueryColumn> Columns { get; init; }

    public required string RowsPath { get; init; }

    public required long TotalRows { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required DateTimeOffset ExpiresAt { get; init; }
}
