using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Core.Storage;
using McpDatabaseQueryApp.Server.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Completions;

public sealed class CompletionRouter
{
    private const int MaxResults = 100;

    private readonly IConnectionRegistry _registry;
    private readonly IProviderRegistry _providers;
    private readonly IMetadataStore _metadata;
    private readonly IScriptStore _scripts;
    private readonly MetadataCache _cache;

    public CompletionRouter(
        IConnectionRegistry registry,
        IProviderRegistry providers,
        IMetadataStore metadata,
        IScriptStore scripts,
        MetadataCache cache)
    {
        _registry = registry;
        _providers = providers;
        _metadata = metadata;
        _scripts = scripts;
        _cache = cache;
    }

    public async ValueTask<CompleteResult> HandleAsync(
        RequestContext<CompleteRequestParams> context,
        CancellationToken cancellationToken)
    {
        if (context.Params is not { } parameters)
        {
            return Empty();
        }

        var argumentName = parameters.Argument?.Name ?? string.Empty;
        var argumentValue = parameters.Argument?.Value ?? string.Empty;
        var contextArgs = parameters.Context?.Arguments as IReadOnlyDictionary<string, string>
            ?? parameters.Context?.Arguments?.ToDictionary(kv => kv.Key, kv => kv.Value);

        var matches = parameters.Ref switch
        {
            ResourceTemplateReference resourceRef => await CompleteForResourceAsync(resourceRef, argumentName, argumentValue, contextArgs, cancellationToken).ConfigureAwait(false),
            PromptReference => await CompleteForPromptArgumentAsync(argumentName, argumentValue, cancellationToken).ConfigureAwait(false),
            _ => [],
        };

        var filtered = matches
            .Where(v => string.IsNullOrEmpty(argumentValue) || v.StartsWith(argumentValue, StringComparison.OrdinalIgnoreCase))
            .Take(MaxResults)
            .ToList();

        return new CompleteResult
        {
            Completion = new Completion
            {
                Values = filtered,
                Total = filtered.Count,
                HasMore = false,
            },
        };
    }

    private async Task<IReadOnlyList<string>> CompleteForResourceAsync(
        ResourceTemplateReference reference,
        string argumentName,
        string argumentValue,
        IReadOnlyDictionary<string, string>? contextArgs,
        CancellationToken cancellationToken)
    {
        _ = argumentValue;
        var uri = reference.Uri ?? string.Empty;
        if (uri.StartsWith("mcpdb://connections/", StringComparison.Ordinal))
        {
            if (argumentName.Equals("id", StringComparison.OrdinalIgnoreCase))
            {
                return [.. _registry.List().Select(c => c.Id)];
            }

            if (argumentName.Equals("schema", StringComparison.OrdinalIgnoreCase)
                && contextArgs?.TryGetValue("id", out var connectionId) == true
                && _registry.TryGet(connectionId, out var connection))
            {
                var meta = _cache.GetOrCreate(connection.Id);
                if (meta.Schemas.Count == 0)
                {
                    meta.Schemas = await connection.ListSchemasAsync(cancellationToken).ConfigureAwait(false);
                }

                return [.. meta.Schemas.Select(s => s.Name)];
            }

            if (argumentName.Equals("table", StringComparison.OrdinalIgnoreCase)
                && contextArgs?.TryGetValue("id", out var cid) == true
                && contextArgs?.TryGetValue("schema", out var schema) == true
                && _registry.TryGet(cid, out var conn))
            {
                var meta = _cache.GetOrCreate(conn.Id);
                if (!meta.Tables.TryGetValue(schema, out var tables))
                {
                    var (fresh, _) = await conn.ListTablesAsync(schema, new PageRequest(0, 500), cancellationToken).ConfigureAwait(false);
                    tables = fresh;
                    var rewritten = new Dictionary<string, IReadOnlyList<TableInfo>>(meta.Tables, StringComparer.OrdinalIgnoreCase)
                    {
                        [schema] = tables,
                    };
                    meta.Tables = rewritten;
                }

                return [.. tables.Select(t => t.Name)];
            }
        }

        if (uri.StartsWith("mcpdb://databases/", StringComparison.Ordinal)
            && argumentName.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            var (items, _) = await _metadata.ListDatabasesAsync(0, 500, null, cancellationToken).ConfigureAwait(false);
            return [.. items.Select(d => d.Name)];
        }

        if (uri.StartsWith("mcpdb://scripts/", StringComparison.Ordinal)
            && argumentName.Equals("name", StringComparison.OrdinalIgnoreCase))
        {
            var (items, _) = await _scripts.ListAsync(0, 500, null, cancellationToken).ConfigureAwait(false);
            return [.. items.Select(s => s.Name)];
        }

        return [];
    }

    private async Task<IReadOnlyList<string>> CompleteForPromptArgumentAsync(
        string argumentName,
        string argumentValue,
        CancellationToken cancellationToken)
    {
        _ = argumentValue;
        if (argumentName.Equals("connectionId", StringComparison.OrdinalIgnoreCase))
        {
            return [.. _registry.List().Select(c => c.Id)];
        }

        if (argumentName.Equals("provider", StringComparison.OrdinalIgnoreCase))
        {
            return [.. _providers.All.Select(p => p.Kind.ToString())];
        }

        if (argumentName.Equals("nameOrId", StringComparison.OrdinalIgnoreCase))
        {
            var (items, _) = await _scripts.ListAsync(0, 500, null, cancellationToken).ConfigureAwait(false);
            return [.. items.Select(s => s.Name)];
        }

        return [];
    }

    private static CompleteResult Empty() => new()
    {
        Completion = new Completion
        {
            Values = [],
            Total = 0,
            HasMore = false,
        },
    };
}
