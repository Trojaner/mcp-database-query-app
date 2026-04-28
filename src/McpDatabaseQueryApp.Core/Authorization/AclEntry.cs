using McpDatabaseQueryApp.Core.Profiles;

namespace McpDatabaseQueryApp.Core.Authorization;

/// <summary>
/// One row of the per-profile access control list. An entry binds a subject
/// (today: a profile id) to an operation set and a scope, with an effect that
/// either permits or forbids the operation on every object the scope matches.
/// </summary>
/// <param name="Id">Stable opaque id for the entry.</param>
/// <param name="ProfileId">Profile the entry applies to. The evaluator only
/// considers entries whose profile matches the request's ambient profile.</param>
/// <param name="SubjectKind">Reserved subject discriminator. Must be
/// <see cref="AclSubjectKind.Profile"/> at present.</param>
/// <param name="Scope">Object scope the entry covers. Wildcards are nulls.</param>
/// <param name="AllowedOperations">Bitmask of operations the entry covers.
/// The evaluator only considers the entry when at least one bit overlaps the
/// request's operation.</param>
/// <param name="Effect"><see cref="AclEffect.Allow"/> or <see cref="AclEffect.Deny"/>.</param>
/// <param name="Priority">Higher priorities outrank lower ones; ties favour
/// <see cref="AclEffect.Deny"/>. Default is <c>0</c>.</param>
/// <param name="Description">Optional free-form note for operators.</param>
public sealed record AclEntry(
    AclEntryId Id,
    ProfileId ProfileId,
    AclSubjectKind SubjectKind,
    AclObjectScope Scope,
    AclOperation AllowedOperations,
    AclEffect Effect,
    int Priority,
    string? Description);
