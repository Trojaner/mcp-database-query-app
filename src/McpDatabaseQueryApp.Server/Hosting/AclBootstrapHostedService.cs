using System.Collections.Concurrent;
using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Profiles;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDatabaseQueryApp.Server.Hosting;

/// <summary>
/// Loads <see cref="AuthorizationOptions.StaticEntries"/> at startup,
/// validates each row, and exposes them to <see cref="AclEvaluator"/> via
/// <see cref="IAclStaticEntrySource"/>. Static entries are config-driven and
/// IMMUTABLE at runtime: they live in <c>appsettings.json</c> only, never
/// flow into SQLite, and the REST API surface (Task 6) cannot delete or
/// modify them. Operators change them by editing config and restarting.
/// </summary>
/// <remarks>
/// To keep them visibly outranking operator-managed rules, every static
/// entry's <see cref="AclEntry.Priority"/> is offset by
/// <see cref="StaticPriorityOffset"/>. A static entry configured with
/// <c>Priority=0</c> ends up evaluated at priority <c>1000</c>, which is
/// higher than any sensible operator-managed value. Static entries also
/// participate in deny-wins tie-breaking exactly like stored entries.
/// </remarks>
public sealed class AclBootstrapHostedService : IHostedService, IAclStaticEntrySource
{
    /// <summary>
    /// Priority offset added to every static entry. Documented as part of the
    /// public contract so operators can predict ordering.
    /// </summary>
    public const int StaticPriorityOffset = 1000;

    private readonly IOptionsMonitor<AuthorizationOptions> _options;
    private readonly ILogger<AclBootstrapHostedService> _logger;
    private readonly ConcurrentDictionary<string, IReadOnlyList<AclEntry>> _byProfile = new(StringComparer.Ordinal);

    /// <summary>Creates a new <see cref="AclBootstrapHostedService"/>.</summary>
    public AclBootstrapHostedService(
        IOptionsMonitor<AuthorizationOptions> options,
        ILogger<AclBootstrapHostedService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        Reload(_options.CurrentValue);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public IReadOnlyList<AclEntry> GetEntriesFor(ProfileId profile)
    {
        return _byProfile.TryGetValue(profile.Value, out var list)
            ? list
            : Array.Empty<AclEntry>();
    }

    /// <summary>
    /// Re-reads <see cref="AuthorizationOptions.StaticEntries"/>. Exposed for
    /// integration tests; the production hosted lifecycle calls this once on
    /// <see cref="StartAsync"/>.
    /// </summary>
    public void Reload(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var grouped = new Dictionary<string, List<AclEntry>>(StringComparer.Ordinal);
        var skipped = 0;
        for (var i = 0; i < options.StaticEntries.Count; i++)
        {
            var cfg = options.StaticEntries[i];
            if (string.IsNullOrWhiteSpace(cfg.ProfileId))
            {
                _logger.LogWarning("Static ACL entry at index {Index} has empty ProfileId; skipping.", i);
                skipped++;
                continue;
            }

            var ops = AclOperation.None;
            for (var j = 0; j < cfg.AllowedOperations.Count; j++)
            {
                ops |= cfg.AllowedOperations[j];
            }

            if (ops == AclOperation.None)
            {
                _logger.LogWarning(
                    "Static ACL entry at index {Index} for profile '{Profile}' has no allowed operations; skipping.",
                    i,
                    cfg.ProfileId);
                skipped++;
                continue;
            }

            var entry = new AclEntry(
                Id: new AclEntryId($"static_{cfg.ProfileId}_{i}"),
                ProfileId: new ProfileId(cfg.ProfileId),
                SubjectKind: cfg.SubjectKind,
                Scope: new AclObjectScope(
                    Host: cfg.Host,
                    Port: cfg.Port,
                    DatabaseName: cfg.DatabaseName,
                    Schema: cfg.Schema,
                    Table: cfg.Table,
                    Column: cfg.Column),
                AllowedOperations: ops,
                Effect: cfg.Effect,
                Priority: cfg.Priority + StaticPriorityOffset,
                Description: cfg.Description ?? "static ACL entry from configuration");

            if (!grouped.TryGetValue(cfg.ProfileId, out var bucket))
            {
                bucket = new List<AclEntry>();
                grouped[cfg.ProfileId] = bucket;
            }
            bucket.Add(entry);
        }

        _byProfile.Clear();
        foreach (var kvp in grouped)
        {
            _byProfile[kvp.Key] = kvp.Value;
        }

        _logger.LogInformation(
            "Loaded {Count} static ACL entr{Plural} across {Profiles} profile(s); skipped {Skipped}.",
            options.StaticEntries.Count - skipped,
            options.StaticEntries.Count - skipped == 1 ? "y" : "ies",
            grouped.Count,
            skipped);
    }
}
