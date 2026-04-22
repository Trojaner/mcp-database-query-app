using McpDatabaseQueryApp.Core.Providers;

namespace McpDatabaseQueryApp.Core.Results;

public interface IResultSetCache
{
    Task<string> StoreAsync(string connectionId, QueryResult result, CancellationToken cancellationToken);

    Task<ResultSetPage?> GetPageAsync(string resultSetId, int offset, int limit, CancellationToken cancellationToken);
}

public sealed record ResultSetPage(
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    long TotalRows,
    bool HasMore);
