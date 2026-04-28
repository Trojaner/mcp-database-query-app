using System.ComponentModel;
using System.Text;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Server.Elicitation;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class QueryTools
{
    private readonly IConnectionRegistry _registry;
    private readonly IResultLimiter _limiter;
    private readonly IResultSetCache _cache;
    private readonly IElicitationGateway _elicitation;
    private readonly IQueryPipeline _pipeline;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly ILogger<QueryTools> _logger;

    public QueryTools(
        IConnectionRegistry registry,
        IResultLimiter limiter,
        IResultSetCache cache,
        IElicitationGateway elicitation,
        IQueryPipeline pipeline,
        McpDatabaseQueryAppOptions options,
        ILogger<QueryTools> logger)
    {
        _registry = registry;
        _limiter = limiter;
        _cache = cache;
        _elicitation = elicitation;
        _pipeline = pipeline;
        _options = options;
        _logger = logger;
    }

    [McpServerTool(Name = "db_query", ReadOnly = true, UseStructuredContent = true)]
    [McpMeta("ui", JsonValue = "{\"resourceUri\":\"ui://mcp-database-query-app/results.html\",\"visibility\":[\"model\",\"app\"]}")]
    [Description("Runs a parameterised SELECT query. Results above the default row limit are paged and cached as a result set resource.")]
    public async Task<QueryToolResult> QueryAsync(
        McpServer server,
        QueryToolArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        if (!_registry.TryGet(args.ConnectionId, out var connection))
        {
            if (_options.AutoConnect)
            {
                connection = await _registry.GetOrOpenPredefinedAsync(args.ConnectionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new KeyNotFoundException($"Connection '{args.ConnectionId}' not found.");
            }
        }

        int effectiveLimit;
        try
        {
            effectiveLimit = _limiter.Resolve(args.Limit, args.ConfirmUnlimited);
        }
        catch (UnconfirmedUnlimitedResultException)
        {
            var confirmed = await _elicitation.ConfirmAsync(server, "You requested unlimited rows. Confirm execution?", cancellationToken).ConfigureAwait(false);
            if (!confirmed)
            {
                throw;
            }

            effectiveLimit = _limiter.Resolve(args.Limit, confirmedUnlimited: true);
        }

        var pipelineContext = new QueryExecutionContext(
            args.Sql,
            args.Parameters,
            connection,
            QueryExecutionMode.Read,
            confirmDestructive: false,
            confirmUnlimited: args.ConfirmUnlimited);
        pipelineContext.Items[McpDestructiveOperationConfirmer.ContextKey] = server;
        await _pipeline.ExecuteAsync(pipelineContext, cancellationToken).ConfigureAwait(false);

        // Request one extra row so we can detect truncation even if the caller asked for exactly `effectiveLimit`.
        var request = new QueryRequest(
            pipelineContext.Sql,
            pipelineContext.Parameters,
            effectiveLimit == int.MaxValue ? null : effectiveLimit + 1,
            args.TimeoutSeconds);

        var raw = await connection.ExecuteQueryAsync(request, cancellationToken).ConfigureAwait(false);
        var truncated = raw.Rows.Count > effectiveLimit;
        var trimmedRows = truncated ? raw.Rows.Take(effectiveLimit).ToList() : raw.Rows;
        var result = new QueryResult(
            raw.Columns,
            trimmedRows,
            trimmedRows.Count,
            truncated,
            raw.TotalRowsAvailable,
            raw.ExecutionMs);

        string? resultSetId = null;
        if (truncated)
        {
            resultSetId = await _cache.StoreAsync(args.ConnectionId, raw, cancellationToken).ConfigureAwait(false);
        }

        var text = BuildAsciiTable(result);
        return new QueryToolResult(
            args.ConnectionId,
            result.Columns,
            result.Rows,
            result.RowCount,
            result.Truncated,
            result.ExecutionMs,
            resultSetId,
            text);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_query_next_page", ReadOnly = true)]
    [Description("Fetches the next page from a previously cached result set.")]
    public async Task<QueryPageResult> NextPageAsync(
        [Description("result_ id returned by db_query.")] string resultSetId,
        [Description("Row offset within the cached result set.")] int offset,
        [Description("Page size. Defaults to 500.")] int? pageSize,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var size = pageSize ?? 500;
        var page = await _cache.GetPageAsync(resultSetId, offset, size, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Result set '{resultSetId}' has expired or does not exist.");

        return new QueryPageResult(
            resultSetId,
            page.Columns,
            page.Rows,
            offset,
            offset + page.Rows.Count,
            page.TotalRows,
            page.HasMore);
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_execute")]
    [Description("Runs an INSERT/UPDATE/DELETE/DDL statement. Destructive SQL triggers a confirmation elicitation unless confirm=true.")]
    public async Task<ExecuteResult> ExecuteAsync(
        McpServer server,
        ExecuteArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        if (!_registry.TryGet(args.ConnectionId, out var connection))
        {
            if (_options.AutoConnect)
            {
                connection = await _registry.GetOrOpenPredefinedAsync(args.ConnectionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new KeyNotFoundException($"Connection '{args.ConnectionId}' not found.");
            }
        }

        var pipelineContext = new QueryExecutionContext(
            args.Sql,
            args.Parameters,
            connection,
            QueryExecutionMode.Write,
            confirmDestructive: args.Confirm,
            confirmUnlimited: false);
        pipelineContext.Items[McpDestructiveOperationConfirmer.ContextKey] = server;

        try
        {
            await _pipeline.ExecuteAsync(pipelineContext, cancellationToken).ConfigureAwait(false);
        }
        catch (DestructiveOperationCancelledException)
        {
            return new ExecuteResult(args.ConnectionId, RowsAffected: 0, Executed: false);
        }

        var affected = await connection.ExecuteNonQueryAsync(
            new NonQueryRequest(pipelineContext.Sql, pipelineContext.Parameters, args.TimeoutSeconds),
            cancellationToken).ConfigureAwait(false);
        return new ExecuteResult(args.ConnectionId, affected, Executed: true);
        }, _logger).ConfigureAwait(false);
    }

    /// <summary>
    /// Returns the provider's execution plan for the supplied SQL. The
    /// pipeline runs in <see cref="QueryExecutionMode.Explain"/> so the
    /// statement is parsed and validated against read-only invariants but
    /// destructive-statement confirmation is skipped — most engines accept
    /// <c>EXPLAIN</c> over any DML without performing the side effect.
    /// </summary>
    [McpServerTool(Name = "db_explain", ReadOnly = true)]
    [Description("Returns the provider's execution plan for the supplied SQL.")]
    public async Task<ExplainToolResult> ExplainAsync(
        McpServer server,
        ExplainArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);
        if (!_registry.TryGet(args.ConnectionId, out var connection))
        {
            if (_options.AutoConnect)
            {
                connection = await _registry.GetOrOpenPredefinedAsync(args.ConnectionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                throw new KeyNotFoundException($"Connection '{args.ConnectionId}' not found.");
            }
        }

        var pipelineContext = new QueryExecutionContext(
            args.Sql,
            args.Parameters,
            connection,
            QueryExecutionMode.Explain,
            confirmDestructive: false,
            confirmUnlimited: false);
        pipelineContext.Items[McpDestructiveOperationConfirmer.ContextKey] = server;
        await _pipeline.ExecuteAsync(pipelineContext, cancellationToken).ConfigureAwait(false);

        var plan = await connection.ExplainAsync(pipelineContext.Sql, pipelineContext.Parameters, cancellationToken).ConfigureAwait(false);
        return new ExplainToolResult(args.ConnectionId, plan.Format, plan.Plan);
        }, _logger).ConfigureAwait(false);
    }

    private static string BuildAsciiTable(QueryResult result)
    {
        if (result.Rows.Count == 0)
        {
            return $"(no rows, {result.ExecutionMs} ms)";
        }

        var widths = new int[result.Columns.Count];
        for (var i = 0; i < result.Columns.Count; i++)
        {
            widths[i] = result.Columns[i].Name.Length;
        }

        foreach (var row in result.Rows)
        {
            for (var i = 0; i < row.Count; i++)
            {
                var text = row[i]?.ToString() ?? "NULL";
                if (text.Length > 80)
                {
                    text = text[..77] + "...";
                }

                if (text.Length > widths[i])
                {
                    widths[i] = text.Length;
                }
            }
        }

        var sb = new StringBuilder();
        AppendRow(sb, widths, result.Columns.Select(c => c.Name).ToArray());
        AppendSeparator(sb, widths);
        foreach (var row in result.Rows)
        {
            var cells = new string[row.Count];
            for (var i = 0; i < row.Count; i++)
            {
                var text = row[i]?.ToString() ?? "NULL";
                cells[i] = text.Length > 80 ? text[..77] + "..." : text;
            }

            AppendRow(sb, widths, cells);
        }

        sb.Append('(').Append(result.RowCount).Append(" row").Append(result.RowCount == 1 ? ", " : "s, ");
        if (result.Truncated)
        {
            sb.Append("truncated, ");
        }

        sb.Append(result.ExecutionMs).Append(" ms)");
        return sb.ToString();
    }

    private static void AppendRow(StringBuilder sb, int[] widths, string[] cells)
    {
        sb.Append("| ");
        for (var i = 0; i < cells.Length; i++)
        {
            sb.Append(cells[i].PadRight(widths[i]));
            sb.Append(" | ");
        }

        sb.AppendLine();
    }

    private static void AppendSeparator(StringBuilder sb, int[] widths)
    {
        sb.Append('+');
        foreach (var w in widths)
        {
            sb.Append('-', w + 2).Append('+');
        }

        sb.AppendLine();
    }
}

public sealed class QueryToolArgs
{
    public required string ConnectionId { get; set; }

    public required string Sql { get; set; }

    public Dictionary<string, object?>? Parameters { get; set; }

    public int? Limit { get; set; }

    public int? TimeoutSeconds { get; set; }

    [Description("Set to true to confirm unlimited (limit=0) results.")]
    public bool ConfirmUnlimited { get; set; }
}

public sealed class ExecuteArgs
{
    public required string ConnectionId { get; set; }

    public required string Sql { get; set; }

    public Dictionary<string, object?>? Parameters { get; set; }

    public int? TimeoutSeconds { get; set; }

    [Description("Skip the destructive-operation confirmation. Only effective when the server is started with --dangerously-skip-permissions.")]
    public bool Confirm { get; set; }
}

public sealed class ExplainArgs
{
    public required string ConnectionId { get; set; }

    public required string Sql { get; set; }

    public Dictionary<string, object?>? Parameters { get; set; }
}

public sealed record QueryToolResult(
    string ConnectionId,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long ExecutionMs,
    string? ResultSetId,
    string TextTable);

public sealed record QueryPageResult(
    string ResultSetId,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int Offset,
    int NextOffset,
    long TotalRows,
    bool HasMore);

public sealed record ExecuteResult(string ConnectionId, long RowsAffected, bool Executed);

public sealed record ExplainToolResult(string ConnectionId, string Format, string Plan);
