using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace McpDatabaseQueryApp.Core.DependencyInjection;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddMcpDatabaseQueryAppCore(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<McpDatabaseQueryAppOptions>()
            .Bind(configuration.GetSection(McpDatabaseQueryAppOptions.SectionName));

        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<McpDatabaseQueryAppOptions>>().Value);
        services.TryAddSingleton(sp => sp.GetRequiredService<McpDatabaseQueryAppOptions>().Secrets);
        services.TryAddSingleton<IMasterKeyProvider>(sp =>
        {
            var secrets = sp.GetRequiredService<SecretsOptions>();
            var config = sp.GetRequiredService<IConfiguration>();
            return new ConfiguredMasterKeyProvider(secrets, config);
        });
        services.TryAddSingleton<ICredentialProtector, AesGcmCredentialProtector>();
        services.TryAddSingleton<IMetadataStore, SqliteMetadataStore>();
        services.TryAddSingleton<IResultLimiter, ResultLimiter>();
        services.TryAddSingleton<IResultSetCache, FileResultSetCache>();
        services.TryAddSingleton<IScriptStore, MetadataScriptStore>();
        services.TryAddSingleton<INoteStore, MetadataNoteStore>();
        services.TryAddSingleton<IProviderRegistry, ProviderRegistry>();
        services.TryAddSingleton<ConnectionActivityTracker>();
        services.TryAddSingleton<IConnectionRegistry, ConnectionRegistry>();

        return services;
    }
}
