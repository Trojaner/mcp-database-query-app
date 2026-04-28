using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// A single tenant-style filter rule. When <see cref="Scope"/> matches the
/// inbound connection and parsed query, <see cref="Filter"/> is AND-merged
/// into the WHERE clause via the rewriter pipeline.
/// </summary>
/// <param name="Id">Stable rule identifier.</param>
/// <param name="ProfileId">Profile this rule applies to. Rules from one
/// profile never affect queries running under another profile.</param>
/// <param name="Scope">Connection + table tuple the rule binds to.</param>
/// <param name="Filter">Predicate emitter. May be equality, IN-list, or raw SQL.</param>
/// <param name="Source">Whether the rule is static (config-time) or dynamic
/// (SQLite). Static rules cannot be modified through the rule store.</param>
/// <param name="Priority">Higher values run earlier when the engine merges
/// directives. The current implementation only uses priority for stable
/// ordering — the rewriter applies every matching rule regardless of
/// priority.</param>
/// <param name="Description">Optional human-readable description shown in
/// admin tools and logs.</param>
public sealed record IsolationRule(
    IsolationRuleId Id,
    ProfileId ProfileId,
    IsolationScope Scope,
    IsolationFilter Filter,
    IsolationRuleSource Source,
    int Priority,
    string? Description);
