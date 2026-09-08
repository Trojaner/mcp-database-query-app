using System.Globalization;
using McpDatabaseQueryApp.Server.Http;

namespace McpDatabaseQueryApp.Server.Tools;

/// <summary>
/// The <c>output</c> argument shared by the query tools: return the result in
/// the tool response, or park it behind a short pre-authenticated HTTP URL.
/// </summary>
/// <remarks>
/// Modelled as a string rather than an enum to match the rest of the tool
/// surface (see <c>ScriptArgs.Provider</c>) and to keep the generated JSON
/// schema stable regardless of enum serialization settings.
/// </remarks>
public static class OutputMode
{
    /// <summary>Return the result inline, in the tool response. The default.</summary>
    public const string Inline = "inline";

    /// <summary>Return only a URL that serves the JSON result over HTTP.</summary>
    public const string Link = "link";

    /// <summary>Shared <c>[Description]</c> text so every query tool documents the argument identically.</summary>
    public const string ParameterDescription =
        "How to deliver the result: \"inline\" (default) returns it in the tool response, exactly as before; " +
        "\"link\" stores the same JSON on the server and returns only a short pre-authenticated HTTP URL to fetch it from, " +
        "keeping large results out of the model's context. Links resolve for 2 hours by default and are dropped when the server restarts.";

    /// <summary>
    /// Interprets the caller-supplied mode. Null/empty means inline so the
    /// argument stays optional.
    /// </summary>
    /// <exception cref="ArgumentException">The value is neither mode.</exception>
    public static bool WantsLink(string? output)
    {
        if (string.IsNullOrWhiteSpace(output) || string.Equals(output, Inline, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(output, Link, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new ArgumentException(
            $"Unknown output mode '{output}'. Use \"{Inline}\" to get the result in the response, or \"{Link}\" to get an HTTP URL for it.",
            nameof(output));
    }

    /// <summary>
    /// Text-mode replacement for the ASCII table: tells a text-only client what
    /// it got and where the rows actually live.
    /// </summary>
    public static string DescribeLink(int rowCount, bool truncated, long executionMs, ResultLinkDescriptor link)
    {
        ArgumentNullException.ThrowIfNull(link);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{rowCount} row{(rowCount == 1 ? string.Empty : "s")}{(truncated ? " (truncated at the row limit)" : string.Empty)}, {executionMs} ms. Full JSON result: {link.Url} (expires {link.ExpiresAt:u}).");
    }
}
