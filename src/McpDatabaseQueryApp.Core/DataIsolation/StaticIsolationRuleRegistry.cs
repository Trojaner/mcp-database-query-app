using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Thread-safe registry of static (config-driven) <see cref="IsolationRule"/>s.
/// Populated once at startup by <c>IsolationRuleBootstrap</c> and read by the
/// merging SQLite store on every list call.
/// </summary>
/// <remarks>
/// Static rules are immutable from any caller — once added they remain
/// available for the lifetime of the process. Callers may still query the
/// registry directly when a fast in-memory fallback is required.
/// </remarks>
public sealed class StaticIsolationRuleRegistry
{
    private readonly object _gate = new();
    private List<IsolationRule> _rules = new();

    /// <summary>
    /// Replaces the registered static rules with <paramref name="rules"/>.
    /// Intended to be called exactly once during startup.
    /// </summary>
    public void Replace(IEnumerable<IsolationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var list = rules.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Source != IsolationRuleSource.Static)
            {
                throw new ArgumentException(
                    $"StaticIsolationRuleRegistry only accepts rules with Source=Static; rule '{list[i].Id}' is {list[i].Source}.",
                    nameof(rules));
            }
        }
        lock (_gate)
        {
            _rules = list;
        }
    }

    /// <summary>
    /// Returns every static rule, regardless of profile. Callers filter
    /// further by scope and profile.
    /// </summary>
    public IReadOnlyList<IsolationRule> All()
    {
        lock (_gate)
        {
            return _rules.ToArray();
        }
    }

    /// <summary>
    /// Returns the static rules that target <paramref name="profileId"/>.
    /// </summary>
    public IReadOnlyList<IsolationRule> ForProfile(ProfileId profileId)
    {
        lock (_gate)
        {
            var hit = new List<IsolationRule>();
            for (var i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].ProfileId.Value == profileId.Value)
                {
                    hit.Add(_rules[i]);
                }
            }
            return hit;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="id"/> belongs to a registered
    /// static rule.
    /// </summary>
    public bool IsStatic(IsolationRuleId id)
    {
        lock (_gate)
        {
            for (var i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Id.Value == id.Value)
                {
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Looks up a static rule by id. Returns <c>null</c> when the id is not
    /// registered.
    /// </summary>
    public IsolationRule? Get(IsolationRuleId id)
    {
        lock (_gate)
        {
            for (var i = 0; i < _rules.Count; i++)
            {
                if (_rules[i].Id.Value == id.Value)
                {
                    return _rules[i];
                }
            }
            return null;
        }
    }
}
