using McpDatabaseQueryApp.Core;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Profiles;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryParsing;

namespace McpDatabaseQueryApp.Core.Tests.QueryExecution;

internal static class TestFakes
{
    public static ParsedStatement Statement(
        StatementKind kind,
        string text,
        bool isMutation,
        bool isDestructive)
    {
        return new ParsedStatement(
            kind,
            isMutation,
            isDestructive,
            text,
            new SourceRange(0, text.Length),
            actions: [],
            warnings: [],
            providerAst: ProviderAst.None);
    }

    public static ParsedBatch Batch(DatabaseKind dialect, string sql, params ParsedStatement[] statements)
    {
        return new ParsedBatch(dialect, sql, statements, errors: [], providerAst: ProviderAst.None);
    }

    public sealed class FakeParser : IQueryParser
    {
        private readonly Func<string, ParsedBatch> _impl;

        public FakeParser(DatabaseKind kind, Func<string, ParsedBatch> impl)
        {
            Kind = kind;
            _impl = impl;
        }

        public DatabaseKind Kind { get; }

        public ParsedBatch Parse(string sql) => _impl(sql);
    }

    public sealed class FakeParserFactory : IQueryParserFactory
    {
        private readonly Dictionary<DatabaseKind, IQueryParser> _parsers = new();

        public FakeParserFactory Add(IQueryParser parser)
        {
            _parsers[parser.Kind] = parser;
            return this;
        }

        public IQueryParser GetParser(DatabaseKind kind)
            => _parsers.TryGetValue(kind, out var parser)
                ? parser
                : throw new InvalidOperationException($"No fake parser for {kind}.");

        public bool TryGetParser(DatabaseKind kind, out IQueryParser parser)
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

    public sealed class FakeConnection : IDatabaseConnection
    {
        public FakeConnection(string id, DatabaseKind kind, bool isReadOnly)
        {
            Id = id;
            Kind = kind;
            IsReadOnly = isReadOnly;
            Descriptor = new ConnectionDescriptor
            {
                Id = id,
                Name = id,
                Provider = kind,
                Host = "test",
                Database = "test",
                Username = "test",
                SslMode = "Disable",
                ReadOnly = isReadOnly,
            };
        }

        public string Id { get; }
        public DatabaseKind Kind { get; }
        public ConnectionDescriptor Descriptor { get; }
        public bool IsReadOnly { get; }

        public Task<QueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<long> ExecuteNonQueryAsync(NonQueryRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<SchemaInfo>> ListSchemasAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<(IReadOnlyList<TableInfo> Items, long Total)> ListTablesAsync(string? schema, PageRequest page, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<TableDetails> DescribeTableAsync(string schema, string table, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<RoleInfo>> ListRolesAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<DatabaseInfo>> ListDatabasesAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<ExplainResult> ExplainAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task PingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
