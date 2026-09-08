namespace McpDatabaseQueryApp.Server.Http;

/// <summary>
/// Route constants shared by the link factory and the endpoint mapping.
/// </summary>
public static class ResultLinkEndpointRoutes
{
    /// <summary>
    /// Path prefix results are served from. Kept to two characters so the
    /// minted URL stays short enough to paste comfortably.
    /// </summary>
    public const string BasePath = "/r";
}
