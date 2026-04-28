using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Thrown by <c>AclQueryStep</c> when the <see cref="IAclEvaluator"/> denies
/// any action in the parsed batch. The exception is treated as a
/// pipeline-aborting error: no statements run on the underlying connection.
/// </summary>
public sealed class AccessDeniedException : Exception
{
    /// <summary>Profile under which the denied action ran.</summary>
    public ProfileId Profile { get; }

    /// <summary>The operation that was denied.</summary>
    public AclOperation Operation { get; }

    /// <summary>
    /// Object reference that triggered the denial, formatted as
    /// "[host[:port]/]database.schema.table[.column]".
    /// </summary>
    public string Scope { get; }

    /// <summary>
    /// Reason copied from the matching <see cref="AclDecision.Reason"/>.
    /// Suitable for surfacing to the caller verbatim.
    /// </summary>
    public string Reason { get; }

    /// <summary>Creates a new <see cref="AccessDeniedException"/>.</summary>
    public AccessDeniedException(ProfileId profile, AclOperation operation, string scope, string reason)
        : base($"Access denied for profile '{profile.Value}': operation {operation} on {scope} ({reason}).")
    {
        Profile = profile;
        Operation = operation;
        Scope = scope;
        Reason = reason;
    }
}
