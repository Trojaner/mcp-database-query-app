using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpDatabaseQueryApp.Server.Hosting;

/// <summary>
/// Loads <see cref="DataIsolationOptions.StaticRules"/> on application startup
/// and pushes them into the in-memory
/// <see cref="StaticIsolationRuleRegistry"/>. Static rules are immutable
/// after this hosted service finishes starting — there is no reload path.
/// </summary>
/// <remarks>
/// Failures translating an individual config entry are logged and skipped so
/// a single malformed rule does not take the whole server down. The
/// successfully-loaded subset is still applied.
/// </remarks>
public sealed class IsolationRuleBootstrap : IHostedService
{
    private readonly DataIsolationOptions _options;
    private readonly StaticIsolationRuleRegistry _registry;
    private readonly ILogger<IsolationRuleBootstrap> _logger;

    /// <summary>
    /// Creates a new <see cref="IsolationRuleBootstrap"/>.
    /// </summary>
    public IsolationRuleBootstrap(
        DataIsolationOptions options,
        StaticIsolationRuleRegistry registry,
        ILogger<IsolationRuleBootstrap> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _registry = registry;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var rules = new List<IsolationRule>(_options.StaticRules.Count);
        for (var i = 0; i < _options.StaticRules.Count; i++)
        {
            var entry = _options.StaticRules[i];
            try
            {
                rules.Add(MapToRule(entry, i));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Failed to load static isolation rule at index {Index} (table='{Schema}.{Table}'); skipping.",
                    i,
                    entry.Schema,
                    entry.Table);
            }
        }

        _registry.Replace(rules);

        if (rules.Count > 0)
        {
            _logger.LogInformation(
                "Loaded {Count} static data-isolation rule(s) from configuration.",
                rules.Count);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static IsolationRule MapToRule(StaticIsolationRuleOptions entry, int index)
    {
        if (string.IsNullOrWhiteSpace(entry.ProfileId))
        {
            throw new InvalidOperationException("ProfileId is required.");
        }
        if (string.IsNullOrWhiteSpace(entry.Host))
        {
            throw new InvalidOperationException("Host is required.");
        }
        if (entry.Port <= 0)
        {
            throw new InvalidOperationException("Port must be a positive integer.");
        }
        if (string.IsNullOrWhiteSpace(entry.DatabaseName))
        {
            throw new InvalidOperationException("DatabaseName is required.");
        }
        if (string.IsNullOrWhiteSpace(entry.Schema))
        {
            throw new InvalidOperationException("Schema is required.");
        }
        if (string.IsNullOrWhiteSpace(entry.Table))
        {
            throw new InvalidOperationException("Table is required.");
        }

        var id = string.IsNullOrWhiteSpace(entry.Id)
            ? new IsolationRuleId($"static:{entry.ProfileId}:{entry.Schema}.{entry.Table}:{index}")
            : new IsolationRuleId(entry.Id);

        return new IsolationRule(
            id,
            new ProfileId(entry.ProfileId),
            new IsolationScope(entry.Host, entry.Port, entry.DatabaseName, entry.Schema, entry.Table),
            BuildFilter(entry.Filter),
            IsolationRuleSource.Static,
            entry.Priority,
            entry.Description);
    }

    private static IsolationFilter BuildFilter(StaticIsolationFilterOptions filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var kind = filter.Kind?.Trim() ?? string.Empty;
        if (string.Equals(kind, "Equality", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(filter.Column))
            {
                throw new InvalidOperationException("Equality filter requires a Column.");
            }
            return new IsolationFilter.EqualityFilter(filter.Column, filter.Value);
        }
        if (string.Equals(kind, "InList", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(filter.Column))
            {
                throw new InvalidOperationException("InList filter requires a Column.");
            }
            return new IsolationFilter.InListFilter(filter.Column, filter.Values.ToList());
        }
        if (string.Equals(kind, "RawSql", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(filter.Predicate))
            {
                throw new InvalidOperationException("RawSql filter requires a Predicate.");
            }
            return new IsolationFilter.RawSqlFilter(
                filter.Predicate,
                new Dictionary<string, object?>(filter.Parameters, StringComparer.Ordinal));
        }
        throw new InvalidOperationException(
            $"Unsupported isolation filter kind '{filter.Kind}'. Use 'Equality', 'InList' or 'RawSql'.");
    }
}
