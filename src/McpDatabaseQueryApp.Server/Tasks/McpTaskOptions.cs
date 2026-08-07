namespace McpDatabaseQueryApp.Server.Tasks;

/// <summary>
/// Configuration for the MCP tasks extension, bound from
/// <c>McpDatabaseQueryApp:Tasks</c>.
/// </summary>
public sealed class McpTaskOptions
{
    public const string SectionName = "McpDatabaseQueryApp:Tasks";

    /// <summary>
    /// Master switch. When false the server does not advertise the tasks extension and every
    /// tool call runs synchronously, exactly as before the extension was added.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How long a finished task's record is retained so a client that was disconnected while
    /// it ran can still collect the result. Also bounds table growth.
    /// </summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Poll interval advertised to clients on <c>tasks/get</c>. A hint, not a limit.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>How often expired task records are swept.</summary>
    public TimeSpan JanitorPeriod { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Tools the client MAY run as a task. Anything not listed stays synchronous.
    /// </summary>
    /// <remarks>
    /// Deliberately an allow-list rather than a blanket "everything is taskable". Making a
    /// fast tool taskable costs a round trip and buys nothing, and tools whose result the
    /// model needs immediately to continue reasoning are worse as tasks. The defaults are the
    /// operations that actually run long against a real database: arbitrary user SQL, saved
    /// scripts, and full-schema introspection on a large catalogue.
    /// </remarks>
    public IList<string> TaskableTools { get; set; } = new List<string>
    {
        "db_query",
        "db_execute",
        "scripts_run",
        "db_describe_batch",
    };

    /// <summary>
    /// Tools the client MUST run as a task (the server refuses to run them synchronously).
    /// Empty by default: forcing a task on a client that has not opted into the extension
    /// breaks it, so this is opt-in per deployment.
    /// </summary>
    public IList<string> RequiredTaskTools { get; set; } = new List<string>();
}
