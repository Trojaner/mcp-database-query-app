using McpDatabaseQueryApp.Core.Configuration;

namespace McpDatabaseQueryApp.Server.Tools;

/// <summary>
/// Determines whether skip-approval parameters should be honoured.
/// When <see cref="McpDatabaseQueryAppOptions.DangerouslySkipPermissions"/> is false,
/// confirmation elicitations are always triggered regardless of the caller's
/// confirm/skipApproval value.
/// </summary>
public sealed class MutationGuard
{
    private readonly McpDatabaseQueryAppOptions _options;

    public MutationGuard(McpDatabaseQueryAppOptions options)
    {
        _options = options;
    }

    /// <summary>
    /// Returns true only when the caller's confirm flag should be honoured.
    /// When the dangerous flag is off, this always returns false — forcing elicitation.
    /// </summary>
    public bool ShouldSkipElicitation(bool callerConfirm)
        => _options.DangerouslySkipPermissions && callerConfirm;
}
