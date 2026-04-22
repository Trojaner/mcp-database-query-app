using McpDatabaseQueryApp.Core.Providers;
using Microsoft.Extensions.DependencyInjection;

namespace McpDatabaseQueryApp.Providers.SqlServer;

public static class SqlServerServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
        return services;
    }
}
