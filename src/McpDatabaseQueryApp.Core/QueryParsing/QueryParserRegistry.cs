using System.Diagnostics.CodeAnalysis;

namespace McpDatabaseQueryApp.Core.QueryParsing;

/// <summary>
/// Default <see cref="IQueryParserFactory"/> backed by parsers registered
/// in the DI container. Resolved by the registered <see cref="DatabaseKind"/>.
/// </summary>
public sealed class QueryParserRegistry : IQueryParserFactory
{
    private readonly Dictionary<DatabaseKind, IQueryParser> _parsers;

    public QueryParserRegistry(IEnumerable<IQueryParser> parsers)
    {
        ArgumentNullException.ThrowIfNull(parsers);

        _parsers = [];
        foreach (var parser in parsers)
        {
            // Last-one-wins keeps the registry useful when a host
            // registers a custom parser to override the default.
            _parsers[parser.Kind] = parser;
        }
    }

    public IQueryParser GetParser(DatabaseKind kind)
    {
        if (_parsers.TryGetValue(kind, out var parser))
        {
            return parser;
        }
        throw new InvalidOperationException($"No IQueryParser is registered for DatabaseKind.{kind}.");
    }

    public bool TryGetParser(DatabaseKind kind, [NotNullWhen(true)] out IQueryParser? parser)
    {
        return _parsers.TryGetValue(kind, out parser);
    }

    bool IQueryParserFactory.TryGetParser(DatabaseKind kind, out IQueryParser parser)
    {
        if (_parsers.TryGetValue(kind, out var found))
        {
            parser = found;
            return true;
        }
        parser = null!;
        return false;
    }
}
