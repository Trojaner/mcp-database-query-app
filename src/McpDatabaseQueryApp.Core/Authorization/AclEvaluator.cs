using System.Collections.Concurrent;
using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Default <see cref="IAclEvaluator"/> implementation. Walks the entries
/// returned by <see cref="IAclStore.ListAsync"/> for the request profile,
/// merged with static entries from <see cref="IAclStaticEntrySource"/>, and
/// returns the highest-priority match.
/// </summary>
/// <remarks>
/// <para><strong>Matching semantics.</strong></para>
/// <list type="number">
///   <item><description>Only entries whose <see cref="AclEntry.ProfileId"/>
///   equals <see cref="IAclEvaluationRequest.Profile"/> AND whose
///   <see cref="AclEntry.SubjectKind"/> is
///   <see cref="AclSubjectKind.Profile"/> are considered. Other subject
///   kinds are reserved and currently skipped.</description></item>
///   <item><description>For each candidate, every non-null scope field is
///   compared to the request using case-insensitive ordinal equality. Null
///   scope fields wildcard-match. The request supplies <c>Host</c>,
///   <c>Port</c>, <c>DatabaseName</c> from
///   <see cref="IAclEvaluationRequest.ConnectionTarget"/>, and
///   <c>Schema</c>/<c>Table</c> from
///   <see cref="IAclEvaluationRequest.Object"/>.</description></item>
///   <item><description>Column matching: when the request's
///   <see cref="IAclEvaluationRequest.Column"/> is <c>null</c> the entry's
///   <see cref="AclObjectScope.Column"/> MUST also be <c>null</c> — column-
///   scoped entries do not match table-level requests. When the request's
///   column is set, the entry matches if its column field is null
///   (wildcard) or equals the request column (case-insensitive).</description></item>
///   <item><description>An entry passes the operation gate when
///   <c>(entry.AllowedOperations &amp; request.Operation) != 0</c>.</description></item>
///   <item><description>Surviving entries are sorted by
///   <see cref="AclEntry.Priority"/> descending, then with
///   <see cref="AclEffect.Deny"/> ahead of <see cref="AclEffect.Allow"/> at
///   equal priority. The first entry decides the outcome.</description></item>
///   <item><description>If no entry matches, the result is
///   <see cref="AclEffect.Deny"/> with a "no matching entry" reason
///   (default-deny).</description></item>
///   <item><description>The default profile is special-cased upstream:
///   when <see cref="DefaultProfilePolicy.AllowAll"/> is configured the
///   evaluator short-circuits with <see cref="AclEffect.Allow"/> for
///   <see cref="ProfileId.Default"/> regardless of stored entries.</description></item>
/// </list>
/// </remarks>
public sealed class AclEvaluator : IAclEvaluator
{
    private static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;
    private static readonly IReadOnlyList<AclEntry> Empty = Array.Empty<AclEntry>();

    private readonly IAclStore _store;
    private readonly IAclStaticEntrySource? _staticSource;
    private readonly Func<AuthorizationOptions> _options;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _utcNow;

    /// <summary>
    /// Creates a new <see cref="AclEvaluator"/>.
    /// </summary>
    /// <param name="store">Backing store for persisted entries.</param>
    /// <param name="options">Live <see cref="AuthorizationOptions"/> snapshot
    /// supplier (typically <c>IOptionsMonitor</c>).</param>
    /// <param name="staticSource">Optional supplier of config-driven static
    /// entries. <c>null</c> means no static entries are merged.</param>
    /// <param name="utcNow">Time source. Tests may pass a deterministic
    /// clock; production passes <see cref="DateTimeOffset.UtcNow"/>.</param>
    public AclEvaluator(
        IAclStore store,
        Func<AuthorizationOptions> options,
        IAclStaticEntrySource? staticSource = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options;
        _staticSource = staticSource;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Removes the cached entries for <paramref name="profile"/>. Intended
    /// to be called by REST API write paths after a successful mutation so
    /// subsequent requests see the new state without waiting for the TTL.
    /// </summary>
    public void Invalidate(ProfileId profile)
    {
        _cache.TryRemove(profile.Value, out _);
    }

    /// <summary>Drops every cached profile snapshot.</summary>
    public void InvalidateAll() => _cache.Clear();

    /// <inheritdoc />
    public async Task<AclDecision> EvaluateAsync(IAclEvaluationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = _options();

        // Bootstrapping bypass for the default profile.
        if (request.Profile.IsDefault && options.DefaultProfilePolicy == DefaultProfilePolicy.AllowAll)
        {
            return new AclDecision(
                AclEffect.Allow,
                MatchingEntry: null,
                Reason: "default profile bypass (DefaultProfilePolicy=AllowAll).");
        }

        var entries = await GetEntriesAsync(request.Profile, options, cancellationToken).ConfigureAwait(false);

        AclEntry? best = null;
        foreach (var entry in entries)
        {
            if (!Matches(entry, request))
            {
                continue;
            }

            if (best is null || OutranksBest(entry, best))
            {
                best = entry;
            }
        }

        if (best is null)
        {
            return new AclDecision(
                AclEffect.Deny,
                MatchingEntry: null,
                Reason: "no matching ACL entry; default-deny.");
        }

        return new AclDecision(
            best.Effect,
            best,
            BuildReason(best));
    }

    private async Task<IReadOnlyList<AclEntry>> GetEntriesAsync(ProfileId profile, AuthorizationOptions options, CancellationToken cancellationToken)
    {
        var ttl = options.CacheTtl;
        var now = _utcNow();

        if (ttl > TimeSpan.Zero
            && _cache.TryGetValue(profile.Value, out var cached)
            && cached.ExpiresAt > now)
        {
            return cached.Entries;
        }

        var stored = await _store.ListAsync(profile, cancellationToken).ConfigureAwait(false);
        var statics = _staticSource?.GetEntriesFor(profile) ?? Empty;

        IReadOnlyList<AclEntry> merged;
        if (statics.Count == 0)
        {
            merged = stored;
        }
        else
        {
            var list = new List<AclEntry>(stored.Count + statics.Count);
            list.AddRange(stored);
            list.AddRange(statics);
            merged = list;
        }

        if (ttl > TimeSpan.Zero)
        {
            _cache[profile.Value] = new CacheEntry(merged, now + ttl);
        }

        return merged;
    }

    private static bool Matches(AclEntry entry, IAclEvaluationRequest request)
    {
        if (entry.SubjectKind != AclSubjectKind.Profile)
        {
            return false;
        }

        if (entry.ProfileId.Value != request.Profile.Value)
        {
            return false;
        }

        if ((entry.AllowedOperations & request.Operation) == AclOperation.None)
        {
            return false;
        }

        var scope = entry.Scope;

        if (scope.Host is not null && !Ci.Equals(scope.Host, request.ConnectionTarget.Host))
        {
            return false;
        }

        if (scope.Port is not null && scope.Port != request.ConnectionTarget.Port)
        {
            return false;
        }

        if (scope.DatabaseName is not null && !Ci.Equals(scope.DatabaseName, request.ConnectionTarget.Database))
        {
            return false;
        }

        if (scope.Schema is not null && !Ci.Equals(scope.Schema, request.Object.Schema))
        {
            return false;
        }

        if (scope.Table is not null && !Ci.Equals(scope.Table, request.Object.Name))
        {
            return false;
        }

        // Column matching: a column-scoped entry never matches a table-level
        // request (request.Column == null). When request.Column is set, a
        // null entry column wildcards.
        if (scope.Column is not null)
        {
            if (request.Column is null)
            {
                return false;
            }

            if (!Ci.Equals(scope.Column, request.Column))
            {
                return false;
            }
        }

        return true;
    }

    private static bool OutranksBest(AclEntry candidate, AclEntry best)
    {
        if (candidate.Priority != best.Priority)
        {
            return candidate.Priority > best.Priority;
        }

        // Equal priority: Deny outranks Allow.
        if (candidate.Effect != best.Effect)
        {
            return candidate.Effect == AclEffect.Deny;
        }

        return false;
    }

    private static string BuildReason(AclEntry entry)
    {
        var prefix = entry.Effect == AclEffect.Allow ? "allowed by" : "denied by";
        var desc = string.IsNullOrEmpty(entry.Description) ? "ACL entry" : entry.Description;
        return $"{prefix} {desc} (priority {entry.Priority}, id {entry.Id.Value}).";
    }

    private readonly record struct CacheEntry(IReadOnlyList<AclEntry> Entries, DateTimeOffset ExpiresAt);
}
