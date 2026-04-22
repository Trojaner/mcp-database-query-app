using System.Text.Json;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.Storage;

namespace McpDatabaseQueryApp.Core.Results;

public sealed class FileResultSetCache : IResultSetCache
{
    private readonly IMetadataStore _metadata;
    private readonly string _cacheDirectory;
    private readonly TimeSpan _ttl;

    public FileResultSetCache(McpDatabaseQueryAppOptions options, IMetadataStore metadata)
    {
        ArgumentNullException.ThrowIfNull(options);
        _metadata = metadata;
        _cacheDirectory = Path.Combine(
            Path.GetDirectoryName(PathResolver.Resolve(options.MetadataDbPath)) ?? ".",
            "cache");
        Directory.CreateDirectory(_cacheDirectory);
        _ttl = options.ResultSetTtl;
    }

    public async Task<string> StoreAsync(string connectionId, QueryResult result, CancellationToken cancellationToken)
    {
        var id = ConnectionIdFactory.NewResultSetId();
        var filePath = Path.Combine(_cacheDirectory, $"{id}.jsonl");
        await using (var stream = File.Create(filePath))
        await using (var writer = new StreamWriter(stream))
        {
            foreach (var row in result.Rows)
            {
                var json = JsonSerializer.Serialize(row);
                await writer.WriteLineAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        var now = DateTimeOffset.UtcNow;
        await _metadata.InsertResultSetAsync(new ResultSetRecord
        {
            Id = id,
            ConnectionId = connectionId,
            Columns = result.Columns,
            RowsPath = filePath,
            TotalRows = result.TotalRowsAvailable,
            CreatedAt = now,
            ExpiresAt = now.Add(_ttl),
        }, cancellationToken).ConfigureAwait(false);

        return id;
    }

    public async Task<ResultSetPage?> GetPageAsync(string resultSetId, int offset, int limit, CancellationToken cancellationToken)
    {
        var record = await _metadata.GetResultSetAsync(resultSetId, cancellationToken).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        if (record.ExpiresAt <= DateTimeOffset.UtcNow || !File.Exists(record.RowsPath))
        {
            return null;
        }

        var rows = new List<IReadOnlyList<object?>>();
        var skipped = 0;
        var taken = 0;
        await using var stream = File.OpenRead(record.RowsPath);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            if (skipped < offset)
            {
                skipped++;
                continue;
            }

            if (taken >= limit)
            {
                return new ResultSetPage(record.Columns, rows, record.TotalRows, HasMore: true);
            }

            var row = JsonSerializer.Deserialize<object?[]>(line) ?? [];
            rows.Add(row);
            taken++;
        }

        return new ResultSetPage(record.Columns, rows, record.TotalRows, HasMore: false);
    }
}
