using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;

namespace McpDatabaseQueryApp.Server.Tasks;

/// <summary>
/// Forwards <see cref="IMcpTaskStore"/> calls to the DI-resolved
/// <see cref="SqliteMcpTaskStore"/>, which does not exist yet when the MCP server is
/// configured.
/// </summary>
/// <remarks>
/// <para>
/// <c>WithTasks(...)</c> takes a store <i>instance</i>, but MCP registration happens while the
/// <see cref="IServiceCollection"/> is still being populated — there is no
/// <see cref="IServiceProvider"/> to resolve the real store from, and building a throwaway one
/// would duplicate every singleton and break disposal. This proxy is registered instead and
/// bound to the real store once the host is built.
/// </para>
/// <para>
/// Nothing calls into the store before the host is running, so binding after build is safe;
/// calling early throws a clear error rather than silently no-opping.
/// </para>
/// </remarks>
public sealed class DeferredMcpTaskStore : IMcpTaskStore
{
    private readonly Lock _gate = new();
    private IMcpTaskStore? _inner;

    /// <summary>
    /// Raised when a client answers a task's input request. Re-raised from the real store once
    /// bound, so the SDK can subscribe here before that store exists.
    /// </summary>
    public event Action<InputResponseReceivedEventArgs>? InputResponseReceived;

    /// <summary>Binds the proxy to the real store. Called once, immediately after host build.</summary>
    public void Bind(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var resolved = services.GetRequiredService<SqliteMcpTaskStore>();

        lock (_gate)
        {
            if (_inner is not null)
            {
                return;
            }

            // The SDK subscribes to this proxy during WithTasks(...), before the real store
            // exists. Forwarding keeps those subscriptions live once it does.
            resolved.InputResponseReceived += Forward;
            _inner = resolved;
        }
    }

    private void Forward(InputResponseReceivedEventArgs args) => InputResponseReceived?.Invoke(args);

    private IMcpTaskStore Inner
    {
        get
        {
            lock (_gate)
            {
                return _inner ?? throw new InvalidOperationException(
                    $"{nameof(DeferredMcpTaskStore)} was used before {nameof(Bind)} was called. " +
                    "Bind it to the built service provider during startup.");
            }
        }
    }

    /// <inheritdoc/>
    public Task<McpTaskInfo> CreateTaskAsync(CancellationToken cancellationToken) =>
        Inner.CreateTaskAsync(cancellationToken);

    /// <inheritdoc/>
    public Task<McpTaskInfo?> GetTaskAsync(string taskId, CancellationToken cancellationToken) =>
        Inner.GetTaskAsync(taskId, cancellationToken);

    /// <inheritdoc/>
    public Task SetCompletedAsync(string taskId, JsonElement result, CancellationToken cancellationToken) =>
        Inner.SetCompletedAsync(taskId, result, cancellationToken);

    /// <inheritdoc/>
    public Task SetFailedAsync(string taskId, JsonElement error, CancellationToken cancellationToken) =>
        Inner.SetFailedAsync(taskId, error, cancellationToken);

    /// <inheritdoc/>
    public Task<bool> SetCancelledAsync(string taskId, CancellationToken cancellationToken) =>
        Inner.SetCancelledAsync(taskId, cancellationToken);

    /// <inheritdoc/>
    public Task SetInputRequestsAsync(
        string taskId,
        IDictionary<string, InputRequest> inputRequests,
        CancellationToken cancellationToken) =>
        Inner.SetInputRequestsAsync(taskId, inputRequests, cancellationToken);

    /// <inheritdoc/>
    public Task ResolveInputRequestsAsync(
        string taskId,
        IDictionary<string, InputResponse> inputResponses,
        CancellationToken cancellationToken) =>
        Inner.ResolveInputRequestsAsync(taskId, inputResponses, cancellationToken);
}
