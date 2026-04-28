namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Top-level configuration section for data-isolation rules. Bound from
/// <c>McpDatabaseQueryApp:DataIsolation</c> in <c>appsettings.json</c>.
/// </summary>
public sealed class DataIsolationOptions
{
    /// <summary>
    /// Configuration section name relative to
    /// <c>McpDatabaseQueryApp</c>.
    /// </summary>
    public const string SectionName = "McpDatabaseQueryApp:DataIsolation";

    /// <summary>
    /// Static rules supplied at startup. These cannot be modified through
    /// the rule store, the MCP surface or the REST admin API.
    /// </summary>
    public IList<StaticIsolationRuleOptions> StaticRules { get; set; } = new List<StaticIsolationRuleOptions>();
}

/// <summary>
/// Configuration shape for a single static isolation rule.
/// </summary>
public sealed class StaticIsolationRuleOptions
{
    /// <summary>Optional explicit id; omitted entries are auto-generated at load time.</summary>
    public string? Id { get; set; }

    /// <summary>Profile this rule applies to.</summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>Database host (case-insensitive).</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>TCP port.</summary>
    public int Port { get; set; }

    /// <summary>Database/catalog name.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Schema name.</summary>
    public string Schema { get; set; } = string.Empty;

    /// <summary>Table name.</summary>
    public string Table { get; set; } = string.Empty;

    /// <summary>Filter expression to apply.</summary>
    public StaticIsolationFilterOptions Filter { get; set; } = new();

    /// <summary>Higher values are merged earlier when multiple rules apply.</summary>
    public int Priority { get; set; }

    /// <summary>Optional human-readable description.</summary>
    public string? Description { get; set; }
}

/// <summary>
/// Configuration shape for a static rule's filter. <see cref="Kind"/>
/// selects which other properties are read.
/// </summary>
public sealed class StaticIsolationFilterOptions
{
    /// <summary>
    /// Discriminator: <c>Equality</c>, <c>InList</c> or <c>RawSql</c>
    /// (case-insensitive).
    /// </summary>
    public string Kind { get; set; } = "Equality";

    /// <summary>Column name for <see cref="Kind"/> = <c>Equality</c> or <c>InList</c>.</summary>
    public string? Column { get; set; }

    /// <summary>Single value for <see cref="Kind"/> = <c>Equality</c>.</summary>
    public object? Value { get; set; }

    /// <summary>Value list for <see cref="Kind"/> = <c>InList</c>.</summary>
    public IList<object?> Values { get; set; } = new List<object?>();

    /// <summary>Raw boolean SQL expression for <see cref="Kind"/> = <c>RawSql</c>.</summary>
    public string? Predicate { get; set; }

    /// <summary>Optional named parameters referenced by <see cref="Predicate"/>.</summary>
    public IDictionary<string, object?> Parameters { get; set; } = new Dictionary<string, object?>(StringComparer.Ordinal);
}
