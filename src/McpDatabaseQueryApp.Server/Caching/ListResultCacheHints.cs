using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;

namespace McpDatabaseQueryApp.Server.Caching;

/// <summary>
/// Stamps the <c>ttlMs</c> / <c>cacheScope</c> freshness hints that MCP 2026-07-28
/// requires on <c>tools/list</c>, <c>prompts/list</c>, <c>resources/list</c> and
/// <c>resources/read</c> results (SEP-2549), and gives <c>tools/list</c> the
/// deterministic ordering the same revision asks for.
/// </summary>
/// <remarks>
/// <para>
/// These are hints that let a client stop re-polling every list on every turn. They
/// complement, rather than replace, the existing <c>listChanged</c> notifications:
/// a mutation still notifies immediately, the TTL only bounds how stale a client
/// that missed the notification can get.
/// </para>
/// <para>
/// <b>Every scope here is <see cref="CacheScope.Private"/>, deliberately.</b> This
/// server is multi-tenant: connections, saved scripts, notes and the visible schema
/// are all scoped to the caller's profile, and the ACL layer further varies what a
/// given caller may see. <see cref="CacheScope.Public"/> would permit a shared
/// intermediary to serve one profile's listing to another. Do not widen it without
/// a per-response check that the payload is genuinely profile-independent.
/// </para>
/// </remarks>
public static class ListResultCacheHints
{
    /// <summary>
    /// Tool set is fixed at startup — it varies only across deployments, never within one.
    /// </summary>
    private static readonly TimeSpan ToolsTtl = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Prompts are backed by saved scripts, which callers create and delete at runtime.
    /// Mutations fire <c>notifications/prompts/list_changed</c>; this bounds the drift
    /// for clients that missed one.
    /// </summary>
    private static readonly TimeSpan PromptsTtl = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Resources include live connections and cached result sets, which come and go as
    /// queries run and the janitor reaps them.
    /// </summary>
    private static readonly TimeSpan ResourcesTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Resource bodies (schema dumps, result-set pages) are snapshots; a short TTL keeps
    /// a paging client from re-reading the same page repeatedly.
    /// </summary>
    private static readonly TimeSpan ReadResourceTtl = TimeSpan.FromSeconds(15);

    public static IMcpServerBuilder WithListResultCacheHints(this IMcpServerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.WithRequestFilters(filters =>
        {
            filters.AddListToolsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);

                // SEP-2575 minor change 3: a stable order lets clients cache the listing
                // and improves LLM prompt-cache hit rates across turns.
                if (result.Tools is { Count: > 1 })
                {
                    result.Tools = result.Tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                }

                result.TimeToLive = ToolsTtl;
                result.CacheScope = CacheScope.Private;
                return result;
            });

            filters.AddListPromptsFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);
                if (result.Prompts is { Count: > 1 })
                {
                    result.Prompts = result.Prompts.OrderBy(p => p.Name, StringComparer.Ordinal).ToList();
                }

                result.TimeToLive = PromptsTtl;
                result.CacheScope = CacheScope.Private;
                return result;
            });

            filters.AddListResourcesFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);
                result.TimeToLive = ResourcesTtl;
                result.CacheScope = CacheScope.Private;
                return result;
            });

            filters.AddReadResourceFilter(next => async (context, cancellationToken) =>
            {
                var result = await next(context, cancellationToken).ConfigureAwait(false);
                result.TimeToLive = ReadResourceTtl;
                result.CacheScope = CacheScope.Private;
                return result;
            });
        });
    }
}
