namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Indicates where a <see cref="IsolationRule"/> originated. Static rules come
/// from <c>appsettings.json</c> and cannot be mutated through the rule store;
/// dynamic rules live in SQLite and may be modified at runtime through the
/// REST admin API.
/// </summary>
public enum IsolationRuleSource
{
    /// <summary>Loaded from configuration at startup. Immutable from any caller.</summary>
    Static = 0,

    /// <summary>Persisted in SQLite. Modifiable via the admin API.</summary>
    Dynamic = 1,
}
