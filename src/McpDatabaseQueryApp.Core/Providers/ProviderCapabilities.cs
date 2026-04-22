namespace McpDatabaseQueryApp.Core.Providers;

public sealed record ProviderCapabilities(
    bool SupportsSchemas,
    bool SupportsExplainJson,
    bool SupportsListenNotify,
    bool SupportsTemporalTables,
    bool SupportsExtensions,
    bool SupportsAgentJobs,
    IReadOnlyList<string> SystemSchemas,
    IReadOnlyList<string> SqlKeywords);
