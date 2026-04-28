using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Server.Elicitation;
using Microsoft.Extensions.DependencyInjection.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the MCP-bound implementation of
/// <see cref="IDestructiveOperationConfirmer"/>. The Core pipeline takes the
/// dependency through DI; this extension keeps the Server composition root
/// the only project that touches the MCP SDK.
/// </summary>
public static class DestructiveConfirmerServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="McpDestructiveOperationConfirmer"/> as the
    /// transport-bound confirmer. Idempotent.
    /// </summary>
    public static IServiceCollection AddMcpDestructiveOperationConfirmer(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IDestructiveOperationConfirmer, McpDestructiveOperationConfirmer>();
        return services;
    }
}
