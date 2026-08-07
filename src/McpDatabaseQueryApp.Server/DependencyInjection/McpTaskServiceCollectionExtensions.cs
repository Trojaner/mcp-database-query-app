using McpDatabaseQueryApp.Server.Hosting;
using McpDatabaseQueryApp.Server.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ModelContextProtocol.Extensions.Tasks;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Handle returned by <see cref="McpTaskServiceCollectionExtensions.AddMcpTasks"/>, carrying the
/// two things the MCP server registration needs but cannot resolve from DI yet.
/// </summary>
public sealed record McpTaskRegistration(McpTaskOptions Options, DeferredMcpTaskStore Store);

/// <summary>
/// Wires the MCP tasks extension (<c>io.modelcontextprotocol/tasks</c>), which MCP 2026-07-28
/// moved out of the core protocol into an official extension.
/// </summary>
public static class McpTaskServiceCollectionExtensions
{
    /// <summary>
    /// Registers task options, the durable store, the execution policy and the background
    /// maintenance services.
    /// </summary>
    public static McpTaskRegistration AddMcpTasks(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = configuration.GetSection(McpTaskOptions.SectionName).Get<McpTaskOptions>() ?? new McpTaskOptions();

        // Created here rather than resolved, because the MCP server registration below needs
        // the instance while the container is still being built (see DeferredMcpTaskStore).
        var store = new DeferredMcpTaskStore();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<McpTaskExecutionPolicy>();
        services.TryAddSingleton<SqliteMcpTaskStore>();
        services.TryAddSingleton(store);
        services.TryAddSingleton<IMcpTaskStore>(store);

        if (options.Enabled)
        {
            // Reconcile before the janitor sweeps, so interrupted rows are failed rather than
            // silently deleted once their TTL lapses.
            services.AddHostedService<McpTaskReconciler>();
            services.AddHostedService<McpTaskJanitor>();
        }

        return new McpTaskRegistration(options, store);
    }

    /// <summary>
    /// Enables the tasks extension on the MCP server when configuration allows it.
    /// </summary>
    /// <remarks>
    /// When disabled the extension is not registered at all, so the server does not advertise
    /// it — a client learns from <c>server/discover</c> that tasks are unavailable instead of
    /// discovering it by having every task request refused.
    /// </remarks>
    public static IMcpServerBuilder WithMcpTasks(this IMcpServerBuilder builder, McpTaskRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(registration);

        if (!registration.Options.Enabled)
        {
            return builder;
        }

        var policy = new McpTaskExecutionPolicy(registration.Options);
        return builder.WithTasks(
            registration.Store,
            taskOptions => taskOptions.ExecutionModeSelector = policy.Select);
    }

    /// <summary>
    /// Binds the deferred task store to the built container. Must run once during startup,
    /// before the server handles any request.
    /// </summary>
    public static void UseMcpTasks(this IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.GetRequiredService<McpTaskOptions>().Enabled)
        {
            return;
        }

        services.GetRequiredService<DeferredMcpTaskStore>().Bind(services);
    }
}
