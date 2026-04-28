using System.Collections.Generic;

namespace McpDatabaseQueryApp.Server.AdminApi.Models;

/// <summary>
/// Wire representation of an ACL entry. <see cref="Source"/> is either
/// <c>static</c> or <c>dynamic</c>; static entries are read-only and cannot
/// be modified through the admin API.
/// </summary>
public sealed record AclEntryResponse(
    string EntryId,
    string ProfileId,
    string SubjectKind,
    AclScopeDto Scope,
    IReadOnlyList<string> AllowedOperations,
    string Effect,
    int Priority,
    string? Description,
    string Source,
    bool ReadOnly);

/// <summary>
/// Wire representation of an <see cref="Core.Authorization.AclObjectScope"/>.
/// All fields are nullable; null is interpreted as a wildcard.
/// </summary>
public sealed record AclScopeDto(
    string? Host,
    int? Port,
    string? DatabaseName,
    string? Schema,
    string? Table,
    string? Column);

/// <summary>POST body for <c>POST .../acl</c>.</summary>
public sealed record CreateAclEntryRequest(
    string? SubjectKind,
    AclScopeDto Scope,
    IReadOnlyList<string> AllowedOperations,
    string Effect,
    int Priority,
    string? Description);

/// <summary>PATCH body for <c>PATCH .../acl/{entryId}</c>.</summary>
public sealed record PatchAclEntryRequest(
    AclScopeDto? Scope,
    IReadOnlyList<string>? AllowedOperations,
    string? Effect,
    int? Priority,
    string? Description);

/// <summary>PUT body for <c>PUT .../acl</c> (bulk replace).</summary>
public sealed record ReplaceAclEntriesRequest(IReadOnlyList<CreateAclEntryRequest> Entries);
