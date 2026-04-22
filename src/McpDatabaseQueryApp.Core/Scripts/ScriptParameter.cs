namespace McpDatabaseQueryApp.Core.Scripts;

public sealed record ScriptParameter(
    string Name,
    string? Description = null,
    string? DefaultValue = null,
    bool Required = false);
