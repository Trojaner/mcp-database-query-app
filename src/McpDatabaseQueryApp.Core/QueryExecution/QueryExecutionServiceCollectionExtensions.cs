using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryExecution.Steps;
using Microsoft.Extensions.DependencyInjection.Extensions;

// Lives in the Microsoft.Extensions.DependencyInjection namespace so callers
// can `services.AddQueryExecutionPipeline()` without an extra using.
// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// DI helpers for the query-execution pipeline. The Core project owns every
/// step except <see cref="IDestructiveOperationConfirmer"/>, which the
/// transport layer (Server) must register.
/// </summary>
public static class QueryExecutionServiceCollectionExtensions
{
    /// <summary>
    /// Registers the pipeline composition (<see cref="IQueryPipeline"/>),
    /// the AST-driven classifier, and every built-in step. Idempotent and
    /// transport-agnostic — call once during composition before any tool
    /// resolves <see cref="IQueryPipeline"/>.
    /// </summary>
    public static IServiceCollection AddQueryExecutionPipeline(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The pipeline depends on the parser registry; ensure it is wired up.
        services.AddQueryParsing();

        services.TryAddSingleton<IQueryPipeline, QueryPipeline>();
        services.TryAddSingleton<IQueryClassifier, QueryClassifier>();
        services.TryAddSingleton<IQueryExecutionContextAccessor, QueryExecutionContextAccessor>();

        // Steps are transient so a future step that needs scoped state can be
        // swapped in without changing the pipeline contract. They are
        // currently stateless so transient vs singleton is immaterial.
        services.TryAddEnumerable(ServiceDescriptor.Transient<IQueryExecutionStep, ParseQueryStep>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IQueryExecutionStep, ReadOnlyEnforcementStep>());
        services.TryAddEnumerable(ServiceDescriptor.Transient<IQueryExecutionStep, DestructiveConfirmationStep>());

        return services;
    }
}
