namespace McpDatabaseQueryApp.Core.Connections;

public sealed record ConnectionDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required DatabaseKind Provider { get; init; }

    public required string Host { get; init; }

    public int? Port { get; init; }

    public required string Database { get; init; }

    public required string Username { get; init; }

    public required string SslMode { get; init; }

    public bool TrustServerCertificate { get; init; }

    public bool ReadOnly { get; init; }

    public string? DefaultSchema { get; init; }

    public IReadOnlyList<string> Tags { get; init; } = [];

    public IReadOnlyDictionary<string, string>? Extra { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed record RedactedDescriptor(
    string Id,
    string Name,
    string Provider,
    string Host,
    int? Port,
    string Database,
    string Username,
    string SslMode,
    bool TrustServerCertificate,
    bool ReadOnly,
    string? DefaultSchema,
    IReadOnlyList<string> Tags,
    string CreatedAt,
    string UpdatedAt)
{
    public static RedactedDescriptor From(ConnectionDescriptor descriptor) => new(
        descriptor.Id,
        descriptor.Name,
        descriptor.Provider.ToString(),
        descriptor.Host,
        descriptor.Port,
        descriptor.Database,
        descriptor.Username,
        descriptor.SslMode,
        descriptor.TrustServerCertificate,
        descriptor.ReadOnly,
        descriptor.DefaultSchema,
        descriptor.Tags,
        descriptor.CreatedAt.ToString("O"),
        descriptor.UpdatedAt.ToString("O"));
}
