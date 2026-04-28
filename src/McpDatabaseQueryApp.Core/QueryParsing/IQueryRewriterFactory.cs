using System.Diagnostics.CodeAnalysis;

namespace McpDatabaseQueryApp.Core.QueryParsing;

public interface IQueryRewriterFactory
{
    IQueryRewriter GetRewriter(DatabaseKind kind);

    bool TryGetRewriter(DatabaseKind kind, out IQueryRewriter rewriter);
}

public sealed class QueryRewriterRegistry : IQueryRewriterFactory
{
    private readonly Dictionary<DatabaseKind, IQueryRewriter> _rewriters;

    public QueryRewriterRegistry(IEnumerable<IQueryRewriter> rewriters)
    {
        ArgumentNullException.ThrowIfNull(rewriters);
        _rewriters = [];
        foreach (var rewriter in rewriters)
        {
            _rewriters[rewriter.Kind] = rewriter;
        }
    }

    public IQueryRewriter GetRewriter(DatabaseKind kind)
    {
        if (_rewriters.TryGetValue(kind, out var rewriter))
        {
            return rewriter;
        }
        throw new InvalidOperationException($"No IQueryRewriter is registered for DatabaseKind.{kind}.");
    }

    public bool TryGetRewriter(DatabaseKind kind, [NotNullWhen(true)] out IQueryRewriter? rewriter)
        => _rewriters.TryGetValue(kind, out rewriter);

    bool IQueryRewriterFactory.TryGetRewriter(DatabaseKind kind, out IQueryRewriter rewriter)
    {
        if (_rewriters.TryGetValue(kind, out var found))
        {
            rewriter = found;
            return true;
        }
        rewriter = null!;
        return false;
    }
}
