using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.DataIsolation;

/// <summary>
/// Default <see cref="IIsolationRuleEngine"/> implementation. Pulls the
/// matching rule list from <see cref="IIsolationRuleStore"/>, walks the
/// parsed batch's actions, and emits one
/// <see cref="PredicateInjectionDirective"/> per matching rule.
/// </summary>
/// <remarks>
/// <para>
/// The engine produces directives keyed to the table the rule targets, not
/// to the statement that touches it: the rewriter is responsible for
/// applying the same directive to every statement whose FROM clause
/// references the table.
/// </para>
/// <para>
/// Parameter values returned by <see cref="IsolationFilter.ToPredicate"/>
/// are stashed on
/// <see cref="QueryExecutionContext.Parameters"/> via
/// <see cref="QueryExecutionContext.Items"/> using the
/// <see cref="ParametersItemKey"/> entry; the pipeline step copies them
/// onto the SQL request before forwarding.
/// </para>
/// </remarks>
public sealed class IsolationRuleEngine : IIsolationRuleEngine
{
    /// <summary>
    /// Key under which the engine attaches its parameter dictionary to
    /// <see cref="QueryExecutionContext.Items"/>.
    /// </summary>
    public const string ParametersItemKey = "IsolationParameters";

    private readonly IIsolationRuleStore _store;

    /// <summary>
    /// Creates a new <see cref="IsolationRuleEngine"/>.
    /// </summary>
    public IsolationRuleEngine(IIsolationRuleStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RewriteDirective>> BuildDirectivesAsync(
        QueryExecutionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (context.Parsed is null)
        {
            return Array.Empty<RewriteDirective>();
        }

        var profileId = context.Profile?.Id ?? ProfileId.Default;
        var rules = await _store
            .ListAsync(profileId, context.Connection.Descriptor, cancellationToken)
            .ConfigureAwait(false);

        if (rules.Count == 0)
        {
            return Array.Empty<RewriteDirective>();
        }

        var touched = CollectTargets(context.Parsed);
        if (touched.Count == 0)
        {
            return Array.Empty<RewriteDirective>();
        }

        var filterContext = new IsolationFilterContext();
        var directives = new List<RewriteDirective>();
        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);

        foreach (var rule in rules)
        {
            ObjectReference? matchedTarget = null;
            for (var i = 0; i < touched.Count; i++)
            {
                if (rule.Scope.MatchesObject(touched[i]))
                {
                    matchedTarget = touched[i];
                    break;
                }
            }

            if (matchedTarget is null)
            {
                continue;
            }

            var (predicateSql, predicateParameters) = rule.Filter.ToPredicate(filterContext);
            // Bind the directive to the rule's own scope so the rewriter
            // matches against the configured schema, not whatever schema
            // the parser inferred (which may be null when the SQL omits
            // qualification).
            var ruleTarget = new ObjectReference(
                ObjectKind.Table,
                Server: null,
                Database: null,
                Schema: rule.Scope.Schema,
                Name: rule.Scope.Table);

            directives.Add(new PredicateInjectionDirective(ruleTarget, predicateSql));

            foreach (var (k, v) in predicateParameters)
            {
                parameters[k] = v;
            }
        }

        if (parameters.Count > 0)
        {
            context.Items[ParametersItemKey] = parameters;
        }

        return directives;
    }

    private static IReadOnlyList<ObjectReference> CollectTargets(ParsedBatch parsed)
    {
        // Deduplicate by qualified name so a query with the same table
        // appearing in multiple actions produces a single match candidate.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ObjectReference>();
        for (var i = 0; i < parsed.Statements.Count; i++)
        {
            var stmt = parsed.Statements[i];
            for (var j = 0; j < stmt.Actions.Count; j++)
            {
                var target = stmt.Actions[j].Target;
                if (target.Kind != ObjectKind.Table)
                {
                    continue;
                }
                if (seen.Add(target.QualifiedName))
                {
                    list.Add(target);
                }
            }
        }
        return list;
    }
}
