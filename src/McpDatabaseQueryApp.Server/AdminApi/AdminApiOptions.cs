namespace McpDatabaseQueryApp.Server.AdminApi;

/// <summary>
/// Configuration for the admin REST API. Bound from
/// <c>McpDatabaseQueryApp:AdminApi</c>.
/// </summary>
/// <remarks>
/// <para>
/// The admin REST API is the only surface that mutates ACL entries and data
/// isolation rules. It deliberately bypasses every MCP-side safety mechanism
/// (ACL evaluation, data-isolation rewriting, destructive-operation
/// elicitation): operators are trusted, MCP clients are not.
/// </para>
/// <para>
/// The API is opt-in. With <see cref="Enabled"/> false (the default), the
/// host does not map any <c>/admin/*</c> route and the auth scheme is not
/// registered.
/// </para>
/// </remarks>
public sealed class AdminApiOptions
{
    /// <summary>
    /// Configuration section name (relative to
    /// <c>McpDatabaseQueryApp</c>): <c>AdminApi</c>.
    /// </summary>
    public const string SectionName = "McpDatabaseQueryApp:AdminApi";

    /// <summary>
    /// When false (default), the admin API endpoints are not mapped and the
    /// authentication scheme is not registered.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Literal API key value. Strongly discouraged outside of local development
    /// because it ends up in <c>appsettings.json</c>. Prefer
    /// <see cref="ApiKeyRef"/>. When set, a warning is logged at startup.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Indirection to the API key, using the same <c>Scheme:Path</c> shape as
    /// <c>McpDatabaseQueryApp:Secrets:KeyRef</c> (e.g. <c>Env:ADMIN_API_KEY</c>,
    /// <c>UserSecrets:Admin:ApiKey</c>, <c>File:/run/secrets/admin-api-key</c>).
    /// </summary>
    public string? ApiKeyRef { get; set; }

    /// <summary>
    /// When true (default), HTTP requests are rejected with 421 Misdirected
    /// Request and the operator is told to use HTTPS. The MCP transport is
    /// unaffected.
    /// </summary>
    public bool RequireHttps { get; set; } = true;

    /// <summary>
    /// Optional allow-list of <c>Host</c> header values that are permitted to
    /// reach the admin API. Defaults to local-loopback only. Set to an empty
    /// list to disable host filtering.
    /// </summary>
    public IList<string> AllowedHosts { get; set; } = new List<string> { "127.0.0.1", "localhost" };
}
