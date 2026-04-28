using McpDatabaseQueryApp.Core.QueryParsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Lives in the Microsoft.Extensions.DependencyInjection namespace so
// callers can `services.AddQueryParsing()` without an extra using.
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

public static class QueryParsingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the query-parsing registry/factory infrastructure.
    /// Provider-specific parsers and rewriters are registered separately
    /// by their respective <c>Add{Provider}Provider()</c> extensions.
    /// </summary>
    public static IServiceCollection AddQueryParsing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IQueryParserFactory, QueryParserRegistry>();
        services.TryAddSingleton<IQueryRewriterFactory, QueryRewriterRegistry>();

        return services;
    }
}
