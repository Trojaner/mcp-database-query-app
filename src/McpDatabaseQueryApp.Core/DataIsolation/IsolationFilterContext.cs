using System.Globalization;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Per-rewrite scratchpad supplied to
/// <see cref="IsolationFilter.ToPredicate(IsolationFilterContext)"/>. Hands
/// out unique parameter names so multiple rules applied to the same query do
/// not collide on a shared identifier such as <c>@v0</c>.
/// </summary>
/// <remarks>
/// Filters never produce parameter names directly — they always go through
/// <see cref="AllocateParameterName"/>. Doing so is the difference between
/// an additive rewrite and one that silently shadows a previously-injected
/// rule's parameter.
/// </remarks>
public sealed class IsolationFilterContext
{
    private readonly string _prefix;
    private int _next;

    /// <summary>
    /// Creates a new context. <paramref name="prefix"/> is appended with a
    /// monotonic counter to produce parameter names; default is <c>iso</c>.
    /// </summary>
    public IsolationFilterContext(string prefix = "iso")
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            throw new ArgumentException("Parameter prefix must not be null or whitespace.", nameof(prefix));
        }
        _prefix = prefix;
    }

    /// <summary>
    /// Allocates a fresh parameter identifier of the form
    /// <c>{prefix}_{counter}</c> (no leading <c>@</c> — Dapper expects bare
    /// names in the parameter dictionary).
    /// </summary>
    public string AllocateParameterName()
    {
        var name = string.Create(
            CultureInfo.InvariantCulture,
            $"{_prefix}_{_next}");
        _next++;
        return name;
    }
}
