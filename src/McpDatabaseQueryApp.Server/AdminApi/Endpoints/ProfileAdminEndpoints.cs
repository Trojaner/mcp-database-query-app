using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Server.AdminApi.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace McpDatabaseQueryApp.Server.AdminApi.Endpoints;

/// <summary>
/// Maps <c>/admin/v1/profiles</c> CRUD onto <see cref="IProfileStore"/>.
/// </summary>
public static class ProfileAdminEndpoints
{
    /// <summary>
    /// Maps the profile endpoints onto the supplied route group.
    /// </summary>
    public static RouteGroupBuilder MapProfileEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var profiles = group.MapGroup("/profiles").WithTags("Profiles");

        profiles.MapGet("/", ListProfilesAsync)
            .WithSummary("List profiles")
            .Produces<PageResponse<ProfileResponse>>();

        profiles.MapGet("/{profileId}", GetProfileAsync)
            .WithSummary("Get a profile by id")
            .Produces<ProfileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        profiles.MapPost("/", CreateProfileAsync)
            .WithSummary("Create a profile")
            .Produces<ProfileResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        profiles.MapPatch("/{profileId}", PatchProfileAsync)
            .WithSummary("Update a profile")
            .Produces<ProfileResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        profiles.MapDelete("/{profileId}", DeleteProfileAsync)
            .WithSummary("Delete a profile (cascade)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<Ok<PageResponse<ProfileResponse>>> ListProfilesAsync(
        IProfileStore store,
        CancellationToken cancellationToken,
        [FromQuery] int? skip = 0,
        [FromQuery] int? take = 100)
    {
        var s = Math.Max(0, skip ?? 0);
        var t = Math.Clamp(take ?? 100, 1, 1000);

        var all = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var page = all.Skip(s).Take(t).Select(MapProfile).ToList();
        return TypedResults.Ok(new PageResponse<ProfileResponse>(page, all.Count, s, t));
    }

    private static async Task<Results<Ok<ProfileResponse>, ProblemHttpResult>> GetProfileAsync(
        string profileId,
        IProfileStore store,
        CancellationToken cancellationToken)
    {
        var id = ParseProfileId(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        var profile = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return TypedResults.Problem(NotFound($"Profile '{profileId}' was not found."));
        }

        return TypedResults.Ok(MapProfile(profile));
    }

    private static async Task<Results<Created<ProfileResponse>, ProblemHttpResult>> CreateProfileAsync(
        CreateProfileRequest body,
        IProfileStore store,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return TypedResults.Problem(BadRequest("Request body is required."));
        }
        if (string.IsNullOrWhiteSpace(body.ProfileId))
        {
            return TypedResults.Problem(BadRequest("profileId is required."));
        }
        if (string.Equals(body.ProfileId, ProfileId.DefaultValue, StringComparison.Ordinal))
        {
            return TypedResults.Problem(Conflict($"Profile id '{ProfileId.DefaultValue}' is reserved."));
        }
        if (string.IsNullOrWhiteSpace(body.Name))
        {
            return TypedResults.Problem(BadRequest("name is required."));
        }
        if (string.IsNullOrWhiteSpace(body.Subject))
        {
            return TypedResults.Problem(BadRequest("subject is required."));
        }

        ProfileId id;
        try
        {
            id = new ProfileId(body.ProfileId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(BadRequest(ex.Message));
        }

        var existing = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return TypedResults.Problem(Conflict($"Profile '{body.ProfileId}' already exists."));
        }

        var status = ParseStatus(body.Status, out var statusProblem);
        if (statusProblem is not null)
        {
            return TypedResults.Problem(statusProblem);
        }

        var profile = new Profile(
            id,
            body.Name.Trim(),
            body.Subject.Trim(),
            string.IsNullOrWhiteSpace(body.Issuer) ? null : body.Issuer.Trim(),
            DateTimeOffset.UtcNow,
            status,
            body.Metadata is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(body.Metadata, StringComparer.Ordinal));

        var saved = await store.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
        return TypedResults.Created($"/admin/v1/profiles/{id.Value}", MapProfile(saved));
    }

    private static async Task<Results<Ok<ProfileResponse>, ProblemHttpResult>> PatchProfileAsync(
        string profileId,
        PatchProfileRequest body,
        IProfileStore store,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return TypedResults.Problem(BadRequest("Request body is required."));
        }

        var id = ParseProfileId(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        var existing = await store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return TypedResults.Problem(NotFound($"Profile '{profileId}' was not found."));
        }

        var status = existing.Status;
        if (!string.IsNullOrWhiteSpace(body.Status))
        {
            status = ParseStatus(body.Status, out var statusProblem);
            if (statusProblem is not null)
            {
                return TypedResults.Problem(statusProblem);
            }
        }

        var updated = existing with
        {
            Name = string.IsNullOrWhiteSpace(body.Name) ? existing.Name : body.Name.Trim(),
            Status = status,
            Metadata = body.Metadata is null
                ? existing.Metadata
                : new Dictionary<string, string>(body.Metadata, StringComparer.Ordinal),
        };

        var saved = await store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(MapProfile(saved));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteProfileAsync(
        string profileId,
        IProfileStore store,
        IAclEvaluator aclEvaluator,
        CancellationToken cancellationToken)
    {
        var id = ParseProfileId(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }
        if (id.IsDefault)
        {
            return TypedResults.Problem(Conflict("The default profile cannot be deleted."));
        }

        var deleted = await store.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return TypedResults.Problem(NotFound($"Profile '{profileId}' was not found."));
        }

        // Drop any cached ACL state pinned to this profile.
        if (aclEvaluator is AclEvaluator typed)
        {
            typed.Invalidate(id);
        }

        return TypedResults.NoContent();
    }

    internal static ProfileId ParseProfileIdOrProblem(string raw, out ProblemDetails? problem) =>
        ParseProfileId(raw, out problem);

    private static ProfileId ParseProfileId(string raw, out ProblemDetails? problem)
    {
        try
        {
            problem = null;
            return new ProfileId(raw);
        }
        catch (ArgumentException ex)
        {
            problem = BadRequest(ex.Message);
            return ProfileId.Default;
        }
    }

    private static ProfileStatus ParseStatus(string? raw, out ProblemDetails? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ProfileStatus.Active;
        }
        if (Enum.TryParse<ProfileStatus>(raw, ignoreCase: true, out var parsed))
        {
            return parsed;
        }
        problem = BadRequest(
            $"Unknown profile status '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<ProfileStatus>())}.");
        return ProfileStatus.Active;
    }

    internal static ProfileResponse MapProfile(Profile profile) =>
        new(
            profile.Id.Value,
            profile.Name,
            profile.Subject,
            profile.Issuer,
            profile.CreatedAt,
            profile.Status.ToString(),
            profile.Metadata);

    internal static ProblemDetails NotFound(string detail) => new()
    {
        Status = StatusCodes.Status404NotFound,
        Title = "Not Found",
        Detail = detail,
        Type = "https://tools.ietf.org/html/rfc7807",
    };

    internal static ProblemDetails BadRequest(string detail) => new()
    {
        Status = StatusCodes.Status400BadRequest,
        Title = "Bad Request",
        Detail = detail,
        Type = "https://tools.ietf.org/html/rfc7807",
    };

    internal static ProblemDetails Conflict(string detail) => new()
    {
        Status = StatusCodes.Status409Conflict,
        Title = "Conflict",
        Detail = detail,
        Type = "https://tools.ietf.org/html/rfc7807",
    };
}
