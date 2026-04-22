using McpDatabaseQueryApp.Core.Providers;

namespace McpDatabaseQueryApp.Core.Results;

public interface IResultLimiter
{
    int DefaultLimit { get; }

    int MaxLimit { get; }

    int Resolve(int? requestedLimit, bool confirmedUnlimited);

    bool IsUnlimited(int? requestedLimit);

    bool AllowDisable { get; }
}

public sealed class ResultLimiter : IResultLimiter
{
    public ResultLimiter(Configuration.McpDatabaseQueryAppOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        DefaultLimit = options.DefaultResultLimit;
        MaxLimit = options.MaxResultLimit;
        AllowDisable = options.AllowDisableLimit;
    }

    public int DefaultLimit { get; }

    public int MaxLimit { get; }

    public bool AllowDisable { get; }

    public bool IsUnlimited(int? requestedLimit) =>
        requestedLimit is 0;

    public int Resolve(int? requestedLimit, bool confirmedUnlimited)
    {
        if (IsUnlimited(requestedLimit))
        {
            if (!AllowDisable)
            {
                throw new InvalidOperationException("Unlimited results are disabled by configuration.");
            }

            if (!confirmedUnlimited)
            {
                throw new UnconfirmedUnlimitedResultException();
            }

            return int.MaxValue;
        }

        var asked = requestedLimit ?? DefaultLimit;
        if (asked < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requestedLimit), asked, "Limit must be non-negative.");
        }

        return Math.Min(asked, MaxLimit);
    }
}

public sealed class UnconfirmedUnlimitedResultException : Exception
{
    public UnconfirmedUnlimitedResultException()
        : base("Unlimited results require explicit user confirmation via elicitation.") { }
}
