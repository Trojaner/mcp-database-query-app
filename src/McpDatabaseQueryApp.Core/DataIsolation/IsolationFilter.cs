using System.Globalization;
using System.Text;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Sealed hierarchy of filter expressions a <see cref="IsolationRule"/> can
/// emit. <see cref="ToPredicate"/> produces an SQL fragment plus the
/// parameter values it references; the rewriter splices the fragment into a
/// <see cref="QueryParsing.PredicateInjectionDirective"/>.
/// </summary>
/// <remarks>
/// <para>
/// Three concrete forms are supported:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="EqualityFilter"/> emits <c>column = @p</c>.</description></item>
///   <item><description><see cref="InListFilter"/> emits <c>column IN (@p0, @p1, …)</c>.</description></item>
///   <item><description><see cref="RawSqlFilter"/> emits an operator-supplied
///   raw predicate; trusts the operator and is intended for static rules
///   only.</description></item>
/// </list>
/// </remarks>
public abstract record IsolationFilter
{
    /// <summary>
    /// Emits the SQL fragment and parameter dictionary for this filter.
    /// </summary>
    /// <param name="context">Allocator that hands out collision-free
    /// parameter names; share a single context per rewrite invocation so
    /// multiple rules don't collide.</param>
    public abstract (string Sql, IReadOnlyDictionary<string, object?> Parameters) ToPredicate(IsolationFilterContext context);

    /// <summary>
    /// Discriminator used by the SQLite store and by tests; matches the
    /// concrete record name without the <c>Filter</c> suffix.
    /// </summary>
    public abstract string Kind { get; }

    private static string QuoteIdentifier(string column)
    {
        // Identifiers come from operator config or REST input. Validate
        // shape rather than dialect-quote: rules don't know whether the
        // host is Postgres or SQL Server. Reject anything that wouldn't
        // be a plain identifier so the predicate stays parseable.
        if (string.IsNullOrEmpty(column))
        {
            throw new ArgumentException("Filter column must not be empty.", nameof(column));
        }
        for (var i = 0; i < column.Length; i++)
        {
            var c = column[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.')
            {
                throw new ArgumentException(
                    $"Filter column '{column}' contains an unsupported character. Use only letters, digits, '_' and '.'.",
                    nameof(column));
            }
        }
        return column;
    }

    /// <summary>
    /// Filters a column to a single value via <c>column = @p</c>.
    /// </summary>
    /// <param name="Column">Column name. Must be a plain identifier.</param>
    /// <param name="Value">Comparison value. Bound through a parameter.</param>
    public sealed record EqualityFilter(string Column, object? Value) : IsolationFilter
    {
        /// <inheritdoc/>
        public override string Kind => "Equality";

        /// <inheritdoc/>
        public override (string Sql, IReadOnlyDictionary<string, object?> Parameters) ToPredicate(IsolationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var name = context.AllocateParameterName();
            var sql = string.Create(
                CultureInfo.InvariantCulture,
                $"{QuoteIdentifier(Column)} = @{name}");
            return (sql, new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [name] = Value,
            });
        }
    }

    /// <summary>
    /// Filters a column to a discrete set of values via
    /// <c>column IN (@p0, @p1, …)</c>. An empty list collapses to
    /// <c>1 = 0</c> so the rule blocks every row rather than failing the
    /// rewrite.
    /// </summary>
    /// <param name="Column">Column name. Must be a plain identifier.</param>
    /// <param name="Values">Comparison values. Each is bound through its own
    /// parameter so Dapper handles type coercion uniformly.</param>
    public sealed record InListFilter(string Column, IReadOnlyList<object?> Values) : IsolationFilter
    {
        /// <inheritdoc/>
        public override string Kind => "InList";

        /// <inheritdoc/>
        public override (string Sql, IReadOnlyDictionary<string, object?> Parameters) ToPredicate(IsolationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(Values);
            if (Values.Count == 0)
            {
                return ("1 = 0", new Dictionary<string, object?>(StringComparer.Ordinal));
            }

            var sb = new StringBuilder(QuoteIdentifier(Column))
                .Append(" IN (");
            var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var i = 0; i < Values.Count; i++)
            {
                if (i > 0)
                {
                    sb.Append(", ");
                }
                var name = context.AllocateParameterName();
                sb.Append('@').Append(name);
                parameters[name] = Values[i];
            }
            sb.Append(')');
            return (sb.ToString(), parameters);
        }
    }

    /// <summary>
    /// Operator-supplied raw boolean expression. Trusts the operator: any
    /// SQL injected here flows directly into the rewritten query. Intended
    /// for static rules only — the dynamic store and admin REST API may
    /// refuse this kind in a future task.
    /// </summary>
    /// <param name="Predicate">Boolean SQL expression. The rewriter wraps
    /// the result in parentheses so callers do not need to.</param>
    /// <param name="Parameters">Optional named parameters referenced by
    /// <paramref name="Predicate"/>. Names are taken verbatim — collisions
    /// with other rules' parameters are the operator's responsibility.</param>
    public sealed record RawSqlFilter(string Predicate, IReadOnlyDictionary<string, object?> Parameters) : IsolationFilter
    {
        /// <inheritdoc/>
        public override string Kind => "RawSql";

        /// <inheritdoc/>
        public override (string Sql, IReadOnlyDictionary<string, object?> Parameters) ToPredicate(IsolationFilterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (string.IsNullOrWhiteSpace(Predicate))
            {
                throw new InvalidOperationException("RawSqlFilter predicate must not be empty.");
            }
            return (Predicate, Parameters ?? new Dictionary<string, object?>(StringComparer.Ordinal));
        }
    }
}
