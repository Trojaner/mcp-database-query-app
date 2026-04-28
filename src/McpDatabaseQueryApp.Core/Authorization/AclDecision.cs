namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Outcome of an <see cref="IAclEvaluator"/> call. Always carries a reason so
/// rejections can be surfaced to operators verbatim and accepted requests can
/// be logged with the matching rule for forensic review.
/// </summary>
/// <param name="Effect">Final effect (Allow or Deny).</param>
/// <param name="MatchingEntry">Entry that produced <see cref="Effect"/>, or
/// <c>null</c> when the decision was the implicit default-deny / built-in
/// allow-all bypass.</param>
/// <param name="Reason">Human-readable explanation suitable for tool error
/// messages and structured logs. Never contains user-supplied query text.</param>
public sealed record AclDecision(
    AclEffect Effect,
    AclEntry? MatchingEntry,
    string Reason)
{
    /// <summary>True when <see cref="Effect"/> is <see cref="AclEffect.Allow"/>.</summary>
    public bool IsAllowed => Effect == AclEffect.Allow;
}
