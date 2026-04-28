namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// Identifies what an <see cref="AclEntry"/>'s subject column refers to.
/// Today only profile-scoped subjects are supported; the additional values
/// are reserved for future role-based extensions and must not be removed
/// once persisted.
/// </summary>
public enum AclSubjectKind
{
    /// <summary>
    /// The entry binds to a specific <see cref="Profiles.ProfileId"/>. The
    /// entry's <c>ProfileId</c> column carries the subject value.
    /// </summary>
    Profile = 0,

    /// <summary>Reserved for future named-role subjects.</summary>
    Role = 1,

    /// <summary>Reserved for future group subjects.</summary>
    Group = 2,
}
