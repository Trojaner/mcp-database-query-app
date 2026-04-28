using McpDatabaseQueryApp.Core.Authorization;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryExecution.Steps;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// Lives in the Microsoft.Extensions.DependencyInjection namespace so callers
// can `services.AddAclAuthorization(configuration)` without an extra using.
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI helpers for the per-profile ACL. The name <c>AddAclAuthorization</c>
/// is deliberately distinct from <c>AddAuthorization</c> in
/// <c>Microsoft.AspNetCore.Authorization</c> so the two can coexist when
/// the host runs on ASP.NET Core.
/// </summary>
public static class AuthorizationServiceCollectionExtensions
{
    /// <summary>
    /// Registers ACL services and the ACL pipeline step. Idempotent.
    /// </summary>
    /// <param name="services">DI container.</param>
    /// <param name="configuration">Root configuration; the
    /// <see cref="AuthorizationOptions"/> binder reads
    /// <c>McpDatabaseQueryApp:Authorization</c>.</param>
    public static IServiceCollection AddAclAuthorization(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AuthorizationOptions>()
            .Bind(configuration.GetSection(AuthorizationOptions.SectionName));

        services.TryAddSingleton<IAclStore, SqliteAclStore>();
        services.TryAddSingleton<IAclEvaluator>(sp =>
        {
            var monitor = sp.GetRequiredService<IOptionsMonitor<AuthorizationOptions>>();
            var staticSource = sp.GetService<IAclStaticEntrySource>();
            var store = sp.GetRequiredService<IAclStore>();
            return new AclEvaluator(store, () => monitor.CurrentValue, staticSource);
        });

        services.TryAddEnumerable(ServiceDescriptor.Transient<IQueryExecutionStep, AclQueryStep>());

        return services;
    }
}
