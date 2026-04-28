namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Outcome encoded by an <see cref="AclEntry"/>. The evaluator selects the
/// highest-priority entry whose scope matches the request and uses its
/// effect as the decision. <see cref="Deny"/> wins ties at equal priority.
/// </summary>
public enum AclEffect
{
    /// <summary>The entry permits the matching operation.</summary>
    Allow = 0,

    /// <summary>The entry forbids the matching operation.</summary>
    Deny = 1,
}
