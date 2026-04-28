using System.Collections.Generic;

namespace McpDatabaseQueryApp.Server.AdminApi.Models;

/// <summary>
/// Wire representation of a profile returned by the admin API. Mirrors
/// <see cref="Core.Profiles.Profile"/> with simple JSON-friendly types.
/// </summary>
public sealed record ProfileResponse(
    string ProfileId,
    string Name,
    string Subject,
    string? Issuer,
    DateTimeOffset CreatedAt,
    string Status,
    IReadOnlyDictionary<string, string> Metadata);

/// <summary>
/// Paged list response shape used by every list endpoint.
/// </summary>
public sealed record PageResponse<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

/// <summary>
/// POST body for <c>POST /admin/v1/profiles</c>.
/// </summary>
public sealed record CreateProfileRequest(
    string ProfileId,
    string Name,
    string Subject,
    string? Issuer,
    string? Status,
    IReadOnlyDictionary<string, string>? Metadata);

/// <summary>
/// PATCH body for <c>PATCH /admin/v1/profiles/{profileId}</c>. Only the
/// supplied fields are updated — null fields leave the existing value alone.
/// Subject / issuer cannot be changed via PATCH; create a new profile instead.
/// </summary>
public sealed record PatchProfileRequest(
    string? Name,
    string? Status,
    IReadOnlyDictionary<string, string>? Metadata);
