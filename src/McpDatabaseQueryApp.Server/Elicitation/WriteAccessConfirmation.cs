using McpDatabaseQueryApp.Server.Tools;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Elicitation;

/// <summary>
/// Gates the grant of write access to a database behind the same explicit
/// confirmation every mutating statement already goes through.
/// </summary>
/// <remarks>
/// <para>
/// A connection that is not <c>ReadOnly</c> lifts the hard block in front of
/// every write path for as long as it lives, and a pre-defined entry with
/// <c>ReadOnly = false</c> hands that out to every later session that connects
/// by name. Granting it is therefore at least as consequential as running a
/// single UPDATE, and is confirmed the same way.
/// </para>
/// <para>
/// <b>Replay safety.</b> Under MRTR the tool body re-runs from the top once the
/// user answers, so callers must ask before performing any side effect — read
/// the current descriptor, decide, confirm, and only then write.
/// </para>
/// </remarks>
public static class WriteAccessConfirmation
{
    /// <summary>MRTR input key for opening a write-enabled connection.</summary>
    public const string ConnectInputKey = "confirm_write_connection";

    /// <summary>MRTR input key for registering a write-enabled pre-defined database.</summary>
    public const string CreateInputKey = "confirm_write_predefined_create";

    /// <summary>MRTR input key for turning read-only off on an existing entry.</summary>
    public const string UpdateInputKey = "confirm_write_predefined_update";

    /// <summary>
    /// Asks the user to approve the write-access grant described by
    /// <paramref name="message"/>, and throws unless they do.
    /// </summary>
    /// <param name="confirm">
    /// The caller's skip-confirmation flag. Only honoured on a host started with
    /// <c>--dangerously-skip-permissions</c>.
    /// </param>
    /// <exception cref="WriteAccessConfirmationRequiredException">
    /// The client offers no way to ask, so the grant fails closed.
    /// </exception>
    /// <exception cref="WriteAccessDeclinedException">The user said no.</exception>
    public static async Task EnsureApprovedAsync(
        IElicitationGateway elicitation,
        MutationGuard mutationGuard,
        RequestContext<CallToolRequestParams> context,
        string inputKey,
        string message,
        bool confirm,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(elicitation);
        ArgumentNullException.ThrowIfNull(mutationGuard);
        ArgumentNullException.ThrowIfNull(context);

        if (mutationGuard.ShouldSkipElicitation(confirm))
        {
            return;
        }

        if (!elicitation.CanElicit(context))
        {
            throw new WriteAccessConfirmationRequiredException(
                $"{message} This grants write access and the connected client does not support elicitation. "
                + "Leave readOnly unset (or true), re-run with confirm=true on a server started with "
                + "--dangerously-skip-permissions, or connect a client that supports elicitation.");
        }

        var approved = await elicitation
            .ConfirmAsync(context, inputKey, message, cancellationToken)
            .ConfigureAwait(false);

        if (!approved)
        {
            throw new WriteAccessDeclinedException("Write access grant declined; nothing was changed.");
        }
    }
}

/// <summary>
/// Thrown when write access was requested but no confirmation channel exists.
/// </summary>
public sealed class WriteAccessConfirmationRequiredException : Exception
{
    public WriteAccessConfirmationRequiredException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Thrown when the user explicitly declines a write-access grant.
/// </summary>
public sealed class WriteAccessDeclinedException : Exception
{
    public WriteAccessDeclinedException(string message)
        : base(message)
    {
    }
}
