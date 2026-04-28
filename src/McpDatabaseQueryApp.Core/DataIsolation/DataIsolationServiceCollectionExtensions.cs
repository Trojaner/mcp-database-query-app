using McpDatabaseQueryApp.Core.DataIsolation;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

// Lives in the Microsoft.Extensions.DependencyInjection namespace so callers
// can `services.AddDataIsolation()` without an extra using.
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI helpers for the data-isolation rule subsystem.
/// </summary>
public static class DataIsolationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the data-isolation engine, store, pipeline step, and
    /// configuration binding. The pipeline step only takes effect when
    /// <c>AddQueryExecutionPipeline()</c> is also called — order is
    /// irrelevant.
    /// </summary>
    public static IServiceCollection AddDataIsolation(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<DataIsolationOptions>()
            .Bind(configuration.GetSection(DataIsolationOptions.SectionName));
        services.TryAddSingleton(sp => sp.GetRequiredService<IOptions<DataIsolationOptions>>().Value);

        services.TryAddSingleton<StaticIsolationRuleRegistry>();
        services.TryAddSingleton<IIsolationRuleStore, SqliteIsolationRuleStore>();
        services.TryAddSingleton<IIsolationRuleEngine, IsolationRuleEngine>();

        services.TryAddEnumerable(ServiceDescriptor.Transient<IQueryExecutionStep, IsolationRuleStep>());

        return services;
    }
}
