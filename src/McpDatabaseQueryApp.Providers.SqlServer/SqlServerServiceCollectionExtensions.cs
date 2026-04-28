using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryParsing;
using McpDatabaseQueryApp.Providers.SqlServer.QueryParsing;
using Microsoft.Extensions.DependencyInjection;

namespace McpDatabaseQueryApp.Providers.SqlServer;

public static class SqlServerServiceCollectionExtensions
{
    public static IServiceCollection AddSqlServerProvider(this IServiceCollection services)
    {
        services.AddSingleton<IDatabaseProvider, SqlServerProvider>();
        services.AddSingleton<IQueryParser, SqlServerQueryParser>();
        services.AddSingleton<IQueryRewriter, SqlServerQueryRewriter>();
        return services;
    }
}
