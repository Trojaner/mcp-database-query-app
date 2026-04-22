using McpDatabaseQueryApp.Core.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace McpDatabaseQueryApp.Providers.Postgres;

public static class PostgresServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProvider, PostgresProvider>();
        return services;
    }
}
