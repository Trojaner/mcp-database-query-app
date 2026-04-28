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
/// Maps <c>/admin/v1/profiles/{profileId}/acl</c> CRUD onto
/// <see cref="IAclStore"/>. Static (config-driven) entries are surfaced as
/// read-only rows so operators can see the merged ruleset; mutations against
/// a static entry return <c>409 Conflict</c>.
/// </summary>
public static class AclAdminEndpoints
{
    /// <summary>Maps the ACL endpoints onto the supplied route group.</summary>
    public static RouteGroupBuilder MapAclEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var acl = group.MapGroup("/profiles/{profileId}/acl").WithTags("ACL");

        acl.MapGet("/", ListAsync)
            .WithSummary("List ACL entries (dynamic + static, read-only)")
            .Produces<IReadOnlyList<AclEntryResponse>>();

        acl.MapGet("/{entryId}", GetAsync)
            .WithSummary("Get one ACL entry")
            .Produces<AclEntryResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        acl.MapPost("/", CreateAsync)
            .WithSummary("Create a dynamic ACL entry")
            .Produces<AclEntryResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        acl.MapPatch("/{entryId}", PatchAsync)
            .WithSummary("Update a dynamic ACL entry")
            .Produces<AclEntryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        acl.MapDelete("/{entryId}", DeleteAsync)
            .WithSummary("Delete a dynamic ACL entry")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        acl.MapPut("/", ReplaceAllAsync)
            .WithSummary("Replace every dynamic ACL entry for the profile")
            .Produces<IReadOnlyList<AclEntryResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<AclEntryResponse>>, ProblemHttpResult>> ListAsync(
        string profileId,
        IAclStore store,
        IProfileStore profileStore,
        IAclStaticEntrySource? staticSource,
        CancellationToken cancellationToken)
    {
        var id = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        if (await profileStore.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.NotFound($"Profile '{profileId}' was not found."));
        }

        var dynamics = await store.ListAsync(id, cancellationToken).ConfigureAwait(false);
        var statics = staticSource?.GetEntriesFor(id) ?? Array.Empty<AclEntry>();

        var items = new List<AclEntryResponse>(dynamics.Count + statics.Count);
        items.AddRange(dynamics.Select(e => Map(e, source: "dynamic", readOnly: false)));
        items.AddRange(statics.Select(e => Map(e, source: "static", readOnly: true)));
        return TypedResults.Ok<IReadOnlyList<AclEntryResponse>>(items);
    }

    private static async Task<Results<Ok<AclEntryResponse>, ProblemHttpResult>> GetAsync(
        string profileId,
        string entryId,
        IAclStore store,
        IAclStaticEntrySource? staticSource,
        CancellationToken cancellationToken)
    {
        var id = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        AclEntryId entryStrong;
        try
        {
            entryStrong = new AclEntryId(entryId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest(ex.Message));
        }

        var entry = await store.GetAsync(entryStrong, cancellationToken).ConfigureAwait(false);
        if (entry is not null && entry.ProfileId.Value == id.Value)
        {
            return TypedResults.Ok(Map(entry, source: "dynamic", readOnly: false));
        }

        var staticEntry = staticSource?
            .GetEntriesFor(id)
            .FirstOrDefault(e => string.Equals(e.Id.Value, entryId, StringComparison.Ordinal));
        if (staticEntry is not null)
        {
            return TypedResults.Ok(Map(staticEntry, source: "static", readOnly: true));
        }

        return TypedResults.Problem(
            ProfileAdminEndpoints.NotFound($"ACL entry '{entryId}' was not found in profile '{profileId}'."));
    }

    private static async Task<Results<Created<AclEntryResponse>, ProblemHttpResult>> CreateAsync(
        string profileId,
        CreateAclEntryRequest body,
        IAclStore store,
        IProfileStore profileStore,
        IAclEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest("Request body is required."));
        }

        var id = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }
        if (await profileStore.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.NotFound($"Profile '{profileId}' was not found."));
        }

        var entry = TryBuild(id, AclEntryId.NewId(), body, out var buildProblem);
        if (entry is null)
        {
            return TypedResults.Problem(buildProblem!);
        }

        var saved = await store.UpsertAsync(entry, cancellationToken).ConfigureAwait(false);
        InvalidateAcl(evaluator);

        return TypedResults.Created(
            $"/admin/v1/profiles/{id.Value}/acl/{saved.Id.Value}",
            Map(saved, source: "dynamic", readOnly: false));
    }

    private static async Task<Results<Ok<AclEntryResponse>, ProblemHttpResult>> PatchAsync(
        string profileId,
        string entryId,
        PatchAclEntryRequest body,
        IAclStore store,
        IAclStaticEntrySource? staticSource,
        IAclEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest("Request body is required."));
        }

        var id = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        if (staticSource?.GetEntriesFor(id).Any(e => string.Equals(e.Id.Value, entryId, StringComparison.Ordinal)) == true)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.Conflict($"ACL entry '{entryId}' is a static entry; modify it through configuration."));
        }

        AclEntryId entryStrong;
        try
        {
            entryStrong = new AclEntryId(entryId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest(ex.Message));
        }

        var existing = await store.GetAsync(entryStrong, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.ProfileId.Value != id.Value)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.NotFound($"ACL entry '{entryId}' was not found in profile '{profileId}'."));
        }

        var ops = existing.AllowedOperations;
        if (body.AllowedOperations is not null)
        {
            if (!TryParseOperations(body.AllowedOperations, out ops, out var opsProblem))
            {
                return TypedResults.Problem(opsProblem!);
            }
        }

        var effect = existing.Effect;
        if (body.Effect is not null && !TryParseEffect(body.Effect, out effect, out var effectProblem))
        {
            return TypedResults.Problem(effectProblem!);
        }

        var scope = body.Scope is null ? existing.Scope : MapScope(body.Scope);

        var updated = new AclEntry(
            existing.Id,
            existing.ProfileId,
            existing.SubjectKind,
            scope,
            ops,
            effect,
            body.Priority ?? existing.Priority,
            body.Description ?? existing.Description);

        var saved = await store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        InvalidateAcl(evaluator);
        return TypedResults.Ok(Map(saved, source: "dynamic", readOnly: false));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string profileId,
        string entryId,
        IAclStore store,
        IAclStaticEntrySource? staticSource,
        IAclEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        var id = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        if (staticSource?.GetEntriesFor(id).Any(e => string.Equals(e.Id.Value, entryId, StringComparison.Ordinal)) == true)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.Conflict($"ACL entry '{entryId}' is a static entry and cannot be deleted at runtime."));
        }

        AclEntryId entryStrong;
        try
        {
            entryStrong = new AclEntryId(entryId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest(ex.Message));
        }

        var deleted = await store.DeleteAsync(id, entryStrong, cancellationToken).ConfigureAwait(false);
        if (!deleted)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.NotFound($"ACL entry '{entryId}' was not found in profile '{profileId}'."));
        }

        InvalidateAcl(evaluator);
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<IReadOnlyList<AclEntryResponse>>, ProblemHttpResult>> ReplaceAllAsync(
        string profileId,
        ReplaceAclEntriesRequest body,
        IAclStore store,
        IProfileStore profileStore,
        IAclEvaluator evaluator,
        CancellationToken cancellationToken)
    {
        if (body is null)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest("Request body is required."));
        }

        var id = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        if (await profileStore.GetAsync(id, cancellationToken).ConfigureAwait(false) is null)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.NotFound($"Profile '{profileId}' was not found."));
        }

        var entries = new List<AclEntry>(body.Entries.Count);
        for (var i = 0; i < body.Entries.Count; i++)
        {
            var built = TryBuild(id, AclEntryId.NewId(), body.Entries[i], out var entryProblem);
            if (built is null)
            {
                return TypedResults.Problem(entryProblem!);
            }
            entries.Add(built);
        }

        await store.ReplaceAllAsync(id, entries, cancellationToken).ConfigureAwait(false);
        InvalidateAcl(evaluator);

        IReadOnlyList<AclEntryResponse> response = entries
            .Select(e => Map(e, source: "dynamic", readOnly: false))
            .ToList();
        return TypedResults.Ok(response);
    }

    private static AclEntry? TryBuild(ProfileId profile, AclEntryId entryId, CreateAclEntryRequest body, out ProblemDetails? problem)
    {
        problem = null;
        if (body is null)
        {
            problem = ProfileAdminEndpoints.BadRequest("Entry body is required.");
            return null;
        }
        if (body.Scope is null)
        {
            problem = ProfileAdminEndpoints.BadRequest("Scope is required.");
            return null;
        }
        if (body.AllowedOperations is null || body.AllowedOperations.Count == 0)
        {
            problem = ProfileAdminEndpoints.BadRequest("allowedOperations must contain at least one value.");
            return null;
        }

        var subjectKind = AclSubjectKind.Profile;
        if (!string.IsNullOrWhiteSpace(body.SubjectKind)
            && !Enum.TryParse(body.SubjectKind, ignoreCase: true, out subjectKind))
        {
            problem = ProfileAdminEndpoints.BadRequest(
                $"Unknown subjectKind '{body.SubjectKind}'. Valid values: {string.Join(", ", Enum.GetNames<AclSubjectKind>())}.");
            return null;
        }
        if (subjectKind != AclSubjectKind.Profile)
        {
            problem = ProfileAdminEndpoints.BadRequest("Only subjectKind='Profile' is supported in the current release.");
            return null;
        }

        if (!TryParseOperations(body.AllowedOperations, out var ops, out var opsProblem))
        {
            problem = opsProblem;
            return null;
        }

        if (!TryParseEffect(body.Effect, out var effect, out var effectProblem))
        {
            problem = effectProblem;
            return null;
        }

        return new AclEntry(
            entryId,
            profile,
            subjectKind,
            MapScope(body.Scope),
            ops,
            effect,
            body.Priority,
            body.Description);
    }

    private static AclObjectScope MapScope(AclScopeDto dto) =>
        new(dto.Host, dto.Port, dto.DatabaseName, dto.Schema, dto.Table, dto.Column);

    private static AclScopeDto MapScopeDto(AclObjectScope scope) =>
        new(scope.Host, scope.Port, scope.DatabaseName, scope.Schema, scope.Table, scope.Column);

    private static bool TryParseOperations(
        IReadOnlyList<string> raw,
        out AclOperation parsed,
        out ProblemDetails? problem)
    {
        parsed = AclOperation.None;
        problem = null;
        for (var i = 0; i < raw.Count; i++)
        {
            var token = raw[i];
            if (string.IsNullOrWhiteSpace(token))
            {
                continue;
            }
            if (!Enum.TryParse<AclOperation>(token, ignoreCase: true, out var op))
            {
                problem = ProfileAdminEndpoints.BadRequest(
                    $"Unknown ACL operation '{token}'. Valid values: {string.Join(", ", Enum.GetNames<AclOperation>())}.");
                return false;
            }
            parsed |= op;
        }
        if (parsed == AclOperation.None)
        {
            problem = ProfileAdminEndpoints.BadRequest("allowedOperations must contain at least one non-None value.");
            return false;
        }
        return true;
    }

    private static bool TryParseEffect(string? raw, out AclEffect parsed, out ProblemDetails? problem)
    {
        parsed = AclEffect.Allow;
        problem = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            problem = ProfileAdminEndpoints.BadRequest("effect is required.");
            return false;
        }
        if (Enum.TryParse(raw, ignoreCase: true, out parsed))
        {
            return true;
        }
        problem = ProfileAdminEndpoints.BadRequest(
            $"Unknown effect '{raw}'. Valid values: {string.Join(", ", Enum.GetNames<AclEffect>())}.");
        return false;
    }

    private static IReadOnlyList<string> OperationsToList(AclOperation ops)
    {
        if (ops == AclOperation.None)
        {
            return new[] { AclOperation.None.ToString() };
        }
        var result = new List<string>();
        foreach (var value in Enum.GetValues<AclOperation>())
        {
            if (value == AclOperation.None || value == AclOperation.All)
            {
                continue;
            }
            if ((ops & value) == value)
            {
                result.Add(value.ToString());
            }
        }
        return result;
    }

    internal static AclEntryResponse Map(AclEntry entry, string source, bool readOnly) =>
        new(
            entry.Id.Value,
            entry.ProfileId.Value,
            entry.SubjectKind.ToString(),
            MapScopeDto(entry.Scope),
            OperationsToList(entry.AllowedOperations),
            entry.Effect.ToString(),
            entry.Priority,
            entry.Description,
            source,
            readOnly);

    private static void InvalidateAcl(IAclEvaluator evaluator)
    {
        if (evaluator is AclEvaluator typed)
        {
            typed.InvalidateAll();
        }
    }
}
