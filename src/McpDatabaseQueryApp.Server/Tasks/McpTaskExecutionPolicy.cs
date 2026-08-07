using ModelContextProtocol.Extensions.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tasks;

/// <summary>
/// Decides, per tool call, whether the call may or must be executed as a long-running task.
/// </summary>
/// <remarks>
/// Backs <see cref="McpTasksOptions.ExecutionModeSelector"/>. The policy is name-based and
/// configuration-driven (see <see cref="McpTaskOptions.TaskableTools"/>) rather than inferred,
/// so which tools are taskable is an explicit, reviewable deployment decision.
/// </remarks>
public sealed class McpTaskExecutionPolicy
{
    private readonly HashSet<string> _taskable;
    private readonly HashSet<string> _required;
    private readonly bool _enabled;

    public McpTaskExecutionPolicy(McpTaskOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _enabled = options.Enabled;
        _taskable = new HashSet<string>(options.TaskableTools ?? [], StringComparer.Ordinal);
        _required = new HashSet<string>(options.RequiredTaskTools ?? [], StringComparer.Ordinal);

        // A tool that must run as a task is necessarily taskable; tolerate a config that
        // lists it only under RequiredTaskTools rather than silently ignoring the entry.
        _taskable.UnionWith(_required);
    }

    public McpTaskExecutionMode Select(RequestContext<CallToolRequestParams> context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Select(context.Params?.Name);
    }

    /// <summary>
    /// Name-based core of the policy. Separated from the <see cref="RequestContext{TParams}"/>
    /// overload so the decision can be exercised without standing up a live MCP server.
    /// </summary>
    public McpTaskExecutionMode Select(string? toolName)
    {
        if (!_enabled)
        {
            return McpTaskExecutionMode.Synchronous;
        }

        var name = toolName;
        if (string.IsNullOrEmpty(name))
        {
            return McpTaskExecutionMode.Synchronous;
        }

        if (_required.Contains(name))
        {
            return McpTaskExecutionMode.Required;
        }

        // Optional means the client chooses: it may request a task, and clients that know
        // nothing about the extension keep getting a plain synchronous result.
        return _taskable.Contains(name)
            ? McpTaskExecutionMode.Optional
            : McpTaskExecutionMode.Synchronous;
    }
}
