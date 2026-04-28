namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Evaluates an <see cref="IAclEvaluationRequest"/> against the ACL entries
/// configured for the request's profile and returns a single
/// <see cref="AclDecision"/>. Implementations are expected to be safe to call
/// concurrently from request threads.
/// </summary>
public interface IAclEvaluator
{
    /// <summary>
    /// Returns the final <see cref="AclDecision"/> for <paramref name="request"/>.
    /// Never throws for missing rules — falls back to the configured default
    /// policy (typically default-deny) instead.
    /// </summary>
    Task<AclDecision> EvaluateAsync(IAclEvaluationRequest request, CancellationToken cancellationToken);
}
