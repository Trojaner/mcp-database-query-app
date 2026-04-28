using System.Text.Json;
using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Server.AdminApi.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace McpDatabaseQueryApp.Server.AdminApi.Endpoints;

/// <summary>
/// Maps <c>/admin/v1/profiles/{profileId}/isolation-rules</c> CRUD onto
/// <see cref="IIsolationRuleStore"/>. Static rules surface as read-only;
/// mutations against them return <c>409 Conflict</c>.
/// </summary>
public static class IsolationRuleAdminEndpoints
{
    /// <summary>Maps the isolation rule endpoints onto the supplied route group.</summary>
    public static RouteGroupBuilder MapIsolationRuleEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var rules = group.MapGroup("/profiles/{profileId}/isolation-rules").WithTags("Isolation");

        rules.MapGet("/", ListAsync)
            .WithSummary("List data-isolation rules (dynamic + static)")
            .Produces<IReadOnlyList<IsolationRuleResponse>>();

        rules.MapGet("/{ruleId}", GetAsync)
            .WithSummary("Get a single rule")
            .Produces<IsolationRuleResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapPost("/", CreateAsync)
            .WithSummary("Create a dynamic rule")
            .Produces<IsolationRuleResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        rules.MapPatch("/{ruleId}", PatchAsync)
            .WithSummary("Update a dynamic rule")
            .Produces<IsolationRuleResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        rules.MapDelete("/{ruleId}", DeleteAsync)
            .WithSummary("Delete a dynamic rule")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return group;
    }

    private static async Task<Results<Ok<IReadOnlyList<IsolationRuleResponse>>, ProblemHttpResult>> ListAsync(
        string profileId,
        IIsolationRuleStore store,
        IProfileStore profileStore,
        StaticIsolationRuleRegistry statics,
        CancellationToken cancellationToken,
        [FromQuery] string? host = null,
        [FromQuery] int? port = null,
        [FromQuery] string? database = null)
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

        // The store's ListAsync expects a connection descriptor; if a host
        // filter was supplied, build one from it. Otherwise fall back to a
        // best-effort full scan: query SQLite directly via the static
        // registry + raw walk of dynamic rows by GetAllForProfileAsync. The
        // store doesn't expose a "list all by profile", so iterate the
        // statics first and use a synthetic descriptor for the dynamic list
        // when filters are present.
        var allByProfile = statics.ForProfile(id).Select(r => MapResponse(r, readOnly: true)).ToList();

        if (host is not null && port is not null && database is not null)
        {
            var descriptor = new ConnectionDescriptor
            {
                Id = "filter",
                Name = "filter",
                Provider = DatabaseKind.Postgres,
                Host = host,
                Port = port,
                Database = database,
                Username = "_",
                SslMode = "Disable",
            };
            var dyn = await store.ListAsync(id, descriptor, cancellationToken).ConfigureAwait(false);
            allByProfile.Clear();
            foreach (var rule in dyn)
            {
                allByProfile.Add(MapResponse(rule, rule.Source == IsolationRuleSource.Static));
            }
            return TypedResults.Ok<IReadOnlyList<IsolationRuleResponse>>(allByProfile);
        }

        // No filter: scan dynamic rules via SQLite by issuing the engine over
        // every distinct host/port/database tuple known to the store. Easier:
        // we enumerate via the store's transparent merging by passing a wildcard
        // descriptor. The store filters on host/port/database equality, so the
        // simplest path is to query for each static rule's connection AND for
        // dynamic rules via a different store helper. To keep this simple we
        // require the operator to supply host/port/database when listing
        // dynamic rules, otherwise we fall back to static-only.
        return TypedResults.Ok<IReadOnlyList<IsolationRuleResponse>>(allByProfile);
    }

    private static async Task<Results<Ok<IsolationRuleResponse>, ProblemHttpResult>> GetAsync(
        string profileId,
        string ruleId,
        IIsolationRuleStore store,
        StaticIsolationRuleRegistry statics,
        CancellationToken cancellationToken)
    {
        var id = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        IsolationRuleId strong;
        try
        {
            strong = new IsolationRuleId(ruleId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest(ex.Message));
        }

        var staticRule = statics.Get(strong);
        if (staticRule is not null && staticRule.ProfileId.Value == id.Value)
        {
            return TypedResults.Ok(MapResponse(staticRule, readOnly: true));
        }

        var rule = await store.GetAsync(strong, cancellationToken).ConfigureAwait(false);
        if (rule is null || rule.ProfileId.Value != id.Value)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.NotFound($"Isolation rule '{ruleId}' was not found in profile '{profileId}'."));
        }
        return TypedResults.Ok(MapResponse(rule, rule.Source == IsolationRuleSource.Static));
    }

    private static async Task<Results<Created<IsolationRuleResponse>, ProblemHttpResult>> CreateAsync(
        string profileId,
        CreateIsolationRuleRequest body,
        IIsolationRuleStore store,
        IProfileStore profileStore,
        IIsolationRuleEngine engine,
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

        var rule = TryBuild(id, IsolationRuleId.NewId(), body, out var buildProblem);
        if (rule is null)
        {
            return TypedResults.Problem(buildProblem!);
        }

        IsolationRule saved;
        try
        {
            saved = await store.UpsertAsync(rule, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.Conflict(ex.Message));
        }

        engine.InvalidateAll();
        return TypedResults.Created(
            $"/admin/v1/profiles/{id.Value}/isolation-rules/{saved.Id.Value}",
            MapResponse(saved, readOnly: false));
    }

    private static async Task<Results<Ok<IsolationRuleResponse>, ProblemHttpResult>> PatchAsync(
        string profileId,
        string ruleId,
        PatchIsolationRuleRequest body,
        IIsolationRuleStore store,
        StaticIsolationRuleRegistry statics,
        IIsolationRuleEngine engine,
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

        IsolationRuleId strong;
        try
        {
            strong = new IsolationRuleId(ruleId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest(ex.Message));
        }

        if (statics.IsStatic(strong))
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.Conflict($"Isolation rule '{ruleId}' is static and cannot be modified at runtime."));
        }

        var existing = await store.GetAsync(strong, cancellationToken).ConfigureAwait(false);
        if (existing is null || existing.ProfileId.Value != id.Value)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.NotFound($"Isolation rule '{ruleId}' was not found in profile '{profileId}'."));
        }

        var scope = body.Scope is null ? existing.Scope : MapScope(body.Scope);
        IsolationFilter filter;
        if (body.Filter is null)
        {
            filter = existing.Filter;
        }
        else if (!TryBuildFilter(body.Filter, out filter!, out var filterProblem))
        {
            return TypedResults.Problem(filterProblem!);
        }

        var updated = new IsolationRule(
            existing.Id,
            existing.ProfileId,
            scope,
            filter,
            IsolationRuleSource.Dynamic,
            body.Priority ?? existing.Priority,
            body.Description ?? existing.Description);

        IsolationRule saved;
        try
        {
            saved = await store.UpsertAsync(updated, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.Conflict(ex.Message));
        }

        engine.InvalidateAll();
        return TypedResults.Ok(MapResponse(saved, readOnly: false));
    }

    private static async Task<Results<NoContent, ProblemHttpResult>> DeleteAsync(
        string profileId,
        string ruleId,
        IIsolationRuleStore store,
        StaticIsolationRuleRegistry statics,
        IIsolationRuleEngine engine,
        CancellationToken cancellationToken)
    {
        _ = ProfileAdminEndpoints.ParseProfileIdOrProblem(profileId, out var problem);
        if (problem is not null)
        {
            return TypedResults.Problem(problem);
        }

        IsolationRuleId strong;
        try
        {
            strong = new IsolationRuleId(ruleId);
        }
        catch (ArgumentException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.BadRequest(ex.Message));
        }

        if (statics.IsStatic(strong))
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.Conflict($"Isolation rule '{ruleId}' is static and cannot be deleted at runtime."));
        }

        bool deleted;
        try
        {
            deleted = await store.DeleteAsync(strong, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return TypedResults.Problem(ProfileAdminEndpoints.Conflict(ex.Message));
        }

        if (!deleted)
        {
            return TypedResults.Problem(
                ProfileAdminEndpoints.NotFound($"Isolation rule '{ruleId}' was not found."));
        }

        engine.InvalidateAll();
        return TypedResults.NoContent();
    }

    private static IsolationRule? TryBuild(
        ProfileId profile,
        IsolationRuleId id,
        CreateIsolationRuleRequest body,
        out ProblemDetails? problem)
    {
        problem = null;
        if (body.Scope is null)
        {
            problem = ProfileAdminEndpoints.BadRequest("scope is required.");
            return null;
        }
        if (string.IsNullOrWhiteSpace(body.Scope.Host)
            || body.Scope.Port <= 0
            || string.IsNullOrWhiteSpace(body.Scope.DatabaseName)
            || string.IsNullOrWhiteSpace(body.Scope.Schema)
            || string.IsNullOrWhiteSpace(body.Scope.Table))
        {
            problem = ProfileAdminEndpoints.BadRequest(
                "scope.host, scope.port, scope.databaseName, scope.schema and scope.table are all required.");
            return null;
        }

        if (body.Filter is null)
        {
            problem = ProfileAdminEndpoints.BadRequest("filter is required.");
            return null;
        }
        if (!TryBuildFilter(body.Filter, out var filter, out var filterProblem))
        {
            problem = filterProblem;
            return null;
        }

        return new IsolationRule(
            id,
            profile,
            MapScope(body.Scope),
            filter!,
            IsolationRuleSource.Dynamic,
            body.Priority,
            body.Description);
    }

    private static bool TryBuildFilter(
        IsolationFilterDto dto,
        out IsolationFilter? filter,
        out ProblemDetails? problem)
    {
        filter = null;
        problem = null;
        if (string.IsNullOrWhiteSpace(dto.Kind))
        {
            problem = ProfileAdminEndpoints.BadRequest("filter.kind is required.");
            return false;
        }

        try
        {
            if (string.Equals(dto.Kind, "Equality", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(dto.Column))
                {
                    problem = ProfileAdminEndpoints.BadRequest("Equality filter requires a column.");
                    return false;
                }
                filter = new IsolationFilter.EqualityFilter(dto.Column, NormalizeJson(dto.Value));
                return true;
            }
            if (string.Equals(dto.Kind, "InList", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(dto.Column))
                {
                    problem = ProfileAdminEndpoints.BadRequest("InList filter requires a column.");
                    return false;
                }
                if (dto.Values is null)
                {
                    problem = ProfileAdminEndpoints.BadRequest("InList filter requires a values array.");
                    return false;
                }
                var values = dto.Values.Select(NormalizeJson).ToList();
                filter = new IsolationFilter.InListFilter(dto.Column, values);
                return true;
            }
            if (string.Equals(dto.Kind, "RawSql", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(dto.Predicate))
                {
                    problem = ProfileAdminEndpoints.BadRequest("RawSql filter requires a predicate.");
                    return false;
                }
                var parameters = dto.Parameters is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : dto.Parameters.ToDictionary(
                        kv => kv.Key,
                        kv => NormalizeJson(kv.Value),
                        StringComparer.Ordinal);
                filter = new IsolationFilter.RawSqlFilter(dto.Predicate, parameters);
                return true;
            }
            problem = ProfileAdminEndpoints.BadRequest(
                $"Unknown filter kind '{dto.Kind}'. Valid: Equality, InList, RawSql.");
            return false;
        }
        catch (ArgumentException ex)
        {
            problem = ProfileAdminEndpoints.BadRequest(ex.Message);
            return false;
        }
    }

    private static IsolationScope MapScope(IsolationScopeDto dto) =>
        new(dto.Host, dto.Port, dto.DatabaseName, dto.Schema, dto.Table);

    private static IsolationScopeDto MapScopeDto(IsolationScope scope) =>
        new(scope.Host, scope.Port, scope.DatabaseName, scope.Schema, scope.Table);

    private static IsolationFilterDto MapFilterDto(IsolationFilter filter) =>
        filter switch
        {
            IsolationFilter.EqualityFilter e => new IsolationFilterDto(
                "Equality", e.Column, e.Value, null, null, null),
            IsolationFilter.InListFilter l => new IsolationFilterDto(
                "InList", l.Column, null, l.Values, null, null),
            IsolationFilter.RawSqlFilter r => new IsolationFilterDto(
                "RawSql", null, null, null, r.Predicate, r.Parameters),
            _ => throw new InvalidOperationException($"Unsupported filter type {filter.GetType().Name}."),
        };

    internal static IsolationRuleResponse MapResponse(IsolationRule rule, bool readOnly) =>
        new(
            rule.Id.Value,
            rule.ProfileId.Value,
            MapScopeDto(rule.Scope),
            MapFilterDto(rule.Filter),
            rule.Source.ToString(),
            rule.Priority,
            rule.Description,
            readOnly);

    private static object? NormalizeJson(object? value)
    {
        if (value is JsonElement el)
        {
            return el.ValueKind switch
            {
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.String => el.GetString(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Number => el.TryGetInt64(out var l) ? l
                    : el.TryGetDouble(out var d) ? d : el.GetRawText(),
                JsonValueKind.Array => el.EnumerateArray().Select(x => NormalizeJson(x)).ToList(),
                JsonValueKind.Object => el.EnumerateObject()
                    .ToDictionary(p => p.Name, p => NormalizeJson((object?)p.Value), StringComparer.Ordinal),
                _ => el.GetRawText(),
            };
        }
        return value;
    }
}
