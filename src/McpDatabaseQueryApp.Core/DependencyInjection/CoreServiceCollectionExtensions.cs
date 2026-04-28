using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Notes;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Core.Scripts;
using McpDatabaseQueryApp.Core.Security;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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

        // Profiles: ambient accessor + store + key provider + per-profile protector.
        services.TryAddSingleton<IProfileContextAccessor, ProfileContextAccessor>();
        services.TryAddSingleton<IProfileStore, SqliteProfileStore>();
        services.TryAddSingleton<IProfileKeyProvider, HkdfProfileKeyProvider>();
        services.TryAddSingleton<IProfileCredentialProtector, ProfileCredentialProtector>();
        services.TryAddSingleton<ProfileResolutionOptions>();
        services.TryAddSingleton<DefaultProfileResolver>();

        // ICredentialProtector is now an ambient adapter that picks the per-profile
        // key from IProfileContextAccessor — existing call sites continue to work
        // unchanged but get profile-scoped keys for free.
        services.TryAddSingleton<ICredentialProtector>(sp =>
            new AmbientProfileCredentialProtector(
                sp.GetRequiredService<IProfileCredentialProtector>(),
                sp.GetRequiredService<IProfileContextAccessor>()));

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
