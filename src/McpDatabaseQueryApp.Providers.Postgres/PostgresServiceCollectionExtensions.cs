using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Providers.Postgres.QueryParsing;
using Microsoft.Extensions.DependencyInjection;

namespace McpDatabaseQueryApp.Providers.Postgres;

/// <summary>
/// DI registration helpers for the PostgreSQL provider.
/// </summary>
public static class PostgresServiceCollectionExtensions
{
    /// <summary>
    /// Registers the PostgreSQL <see cref="IDatabaseProvider"/>, the
    /// libpg_query-backed <see cref="IQueryParser"/>, and the matching
    /// <see cref="IQueryRewriter"/>.
    /// </summary>
    public static IServiceCollection AddPostgresProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProvider, PostgresProvider>();
        services.AddSingleton<IQueryParser, PostgresQueryParser>();
        services.AddSingleton<IQueryRewriter, PostgresQueryRewriter>();
        return services;
    }
}
