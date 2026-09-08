using McpDatabaseQueryApp.Apps;
using System.ComponentModel;
using System.Text;
using McpDatabaseQueryApp.Core.Configuration;
using McpDatabaseQueryApp.Core.Connections;
using McpDatabaseQueryApp.Core.Providers;
using McpDatabaseQueryApp.Core.QueryExecution;
using McpDatabaseQueryApp.Core.Results;
using McpDatabaseQueryApp.Server.Elicitation;
using McpDatabaseQueryApp.Server.Http;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Extensions.Apps;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

// The MCP Apps extension (io.modelcontextprotocol/apps) is still marked experimental by the
// SDK (MCPEXP003). Suppressed at file scope rather than project-wide so that any *other*
// experimental API introduced elsewhere still fails the build and gets a deliberate decision.
#pragma warning disable MCPEXP003

namespace McpDatabaseQueryApp.Server.Tools;

[McpServerToolType]
public sealed class QueryTools
{
    private readonly IConnectionRegistry _registry;
    private readonly IResultLimiter _limiter;
    private readonly IResultSetCache _cache;
    private readonly IElicitationGateway _elicitation;
    private readonly IQueryPipeline _pipeline;
    private readonly ResultLinkFactory _links;
    private readonly McpDatabaseQueryAppOptions _options;
    private readonly ILogger<QueryTools> _logger;

    public QueryTools(
        IConnectionRegistry registry,
        IResultLimiter limiter,
        IResultSetCache cache,
        IElicitationGateway elicitation,
        IQueryPipeline pipeline,
        ResultLinkFactory links,
        McpDatabaseQueryAppOptions options,
        ILogger<QueryTools> logger)
    {
        _registry = registry;
        _limiter = limiter;
        _cache = cache;
        _elicitation = elicitation;
        _pipeline = pipeline;
        _links = links;
        _options = options;
        _logger = logger;
    }

    [McpServerTool(Name = "db_query", ReadOnly = true, UseStructuredContent = true)]
    [McpAppUi(ResourceUri = AppResources.ResultsUri, Visibility = [McpUiToolVisibility.Model, McpUiToolVisibility.App])]
    [Description("Runs a parameterised SELECT query. Results above the default row limit are paged and cached as a result set resource.")]
    public async Task<QueryToolResult> QueryAsync(
        RequestContext<CallToolRequestParams> context,
        QueryToolArgs args,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        ArgumentNullException.ThrowIfNull(args);

        // Parse the delivery mode before touching the database so a typo costs
        // a round-trip rather than a query.
        var asLink = OutputMode.WantsLink(args.Output);
        if (asLink && !string.IsNullOrWhiteSpace(args.CsvPath))
        {
            throw new ArgumentException(
                "output=\"link\" and csvPath are mutually exclusive: the first parks the JSON result behind a URL, the second writes CSV to a file. Pick one.");
        }

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
            var confirmed = await _elicitation.ConfirmAsync(context, "confirm_unlimited", "You requested unlimited rows. Confirm execution?", cancellationToken).ConfigureAwait(false);
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
        pipelineContext.Items[McpDestructiveOperationConfirmer.ContextKey] = context;
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

        // CSV mode: stream the rows to a file instead of returning them inline,
        // so large result sets don't have to travel through the model context.
        // The row count is still bounded by the effective limit — callers who
        // want the full table raise `limit` (or set limit=0 with
        // confirm_unlimited) exactly as they would for an inline query.
        if (!string.IsNullOrWhiteSpace(args.CsvPath))
        {
            var csvPath = PathResolver.Resolve(args.CsvPath);
            var directory = Path.GetDirectoryName(csvPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using (var stream = File.Create(csvPath))
            await using (var writer = new StreamWriter(stream))
            {
                await CsvResultWriter.WriteAsync(
                    writer,
                    result.Columns.Select(c => c.Name),
                    result.Rows,
                    cancellationToken).ConfigureAwait(false);
            }

            var summary = new StringBuilder();
            summary.Append("Wrote ").Append(result.RowCount)
                .Append(result.RowCount == 1 ? " row to " : " rows to ").Append(csvPath);
            if (truncated)
            {
                summary.Append(" (truncated at the row limit — raise `limit` to export more)");
            }

            summary.Append(" [").Append(result.ExecutionMs).Append(" ms]");

            return new QueryToolResult(
                args.ConnectionId,
                result.Columns,
                Rows: [],
                result.RowCount,
                result.Truncated,
                result.ExecutionMs,
                ResultSetId: null,
                summary.ToString(),
                csvPath,
                ResultUrl: null,
                ResultUrlExpiresAt: null);
        }

        string? resultSetId = null;
        if (truncated)
        {
            resultSetId = await _cache.StoreAsync(args.ConnectionId, raw, cancellationToken).ConfigureAwait(false);
        }

        var text = BuildAsciiTable(result);
        var full = new QueryToolResult(
            args.ConnectionId,
            result.Columns,
            result.Rows,
            result.RowCount,
            result.Truncated,
            result.ExecutionMs,
            resultSetId,
            text,
            CsvPath: null,
            ResultUrl: null,
            ResultUrlExpiresAt: null);

        if (!asLink)
        {
            return full;
        }

        // Link mode publishes exactly the payload inline mode would have
        // returned, then hands back the same envelope with the rows stripped —
        // the model keeps the shape and the counts, the bytes stay on the server.
        var link = _links.Create(full, typeof(QueryToolResult));
        return full with
        {
            Rows = [],
            TextTable = OutputMode.DescribeLink(result.RowCount, result.Truncated, result.ExecutionMs, link),
            ResultUrl = link.Url,
            ResultUrlExpiresAt = link.ExpiresAt,
        };
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_query_next_page", ReadOnly = true)]
    [Description("Fetches the next page from a previously cached result set.")]
    public async Task<QueryPageResult> NextPageAsync(
        [Description("result_ id returned by db_query.")] string resultSetId,
        [Description("Row offset within the cached result set.")] int offset,
        [Description("Page size. Defaults to 500.")] int? pageSize,
        [Description(OutputMode.ParameterDescription)] string? output,
        CancellationToken cancellationToken)
    {
        return await ToolErrorHandler.WrapAsync(async () =>
        {
        var asLink = OutputMode.WantsLink(output);
        var size = pageSize ?? 500;
        var page = await _cache.GetPageAsync(resultSetId, offset, size, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Result set '{resultSetId}' has expired or does not exist.");

        var full = new QueryPageResult(
            resultSetId,
            page.Columns,
            page.Rows,
            offset,
            offset + page.Rows.Count,
            page.TotalRows,
            page.HasMore,
            ResultUrl: null,
            ResultUrlExpiresAt: null);

        if (!asLink)
        {
            return full;
        }

        var link = _links.Create(full, typeof(QueryPageResult));
        return full with
        {
            Rows = [],
            ResultUrl = link.Url,
            ResultUrlExpiresAt = link.ExpiresAt,
        };
        }, _logger).ConfigureAwait(false);
    }

    [McpServerTool(Name = "db_execute")]
    [Description("Runs an INSERT/UPDATE/DELETE/DDL statement. Any SQL that changes the database triggers a confirmation elicitation unless confirm=true.")]
    public async Task<ExecuteResult> ExecuteAsync(
        RequestContext<CallToolRequestParams> context,
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
        pipelineContext.Items[McpDestructiveOperationConfirmer.ContextKey] = context;

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
    /// write confirmation is skipped — most engines accept
    /// <c>EXPLAIN</c> over any DML without performing the side effect.
    /// </summary>
    [McpServerTool(Name = "db_explain", ReadOnly = true)]
    [Description("Returns the provider's execution plan for the supplied SQL.")]
    public async Task<ExplainToolResult> ExplainAsync(
        RequestContext<CallToolRequestParams> context,
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
        pipelineContext.Items[McpDestructiveOperationConfirmer.ContextKey] = context;
        await _pipeline.ExecuteAsync(pipelineContext, cancellationToken).ConfigureAwait(false);

        var plan = await connection.ExplainAsync(pipelineContext.Sql, pipelineContext.Parameters, cancellationToken).ConfigureAwait(false);
        var full = new ExplainToolResult(args.ConnectionId, plan.Format, plan.Plan, ResultUrl: null, ResultUrlExpiresAt: null);
        if (!OutputMode.WantsLink(args.Output))
        {
            return full;
        }

        var link = _links.Create(full, typeof(ExplainToolResult));
        return full with
        {
            Plan = $"Plan withheld from this response; fetch the full JSON at {link.Url} (expires {link.ExpiresAt:u}).",
            ResultUrl = link.Url,
            ResultUrlExpiresAt = link.ExpiresAt,
        };
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

    [Description("When set, write the result rows to this CSV file (RFC 4180) instead of returning them inline. Use for large result sets. Relative paths resolve against the server's working directory; raise `limit` (or limit=0 with confirm_unlimited) to control how many rows are exported.")]
    public string? CsvPath { get; set; }

    [Description(OutputMode.ParameterDescription)]
    public string? Output { get; set; }
}

public sealed class ExecuteArgs
{
    public required string ConnectionId { get; set; }

    public required string Sql { get; set; }

    public Dictionary<string, object?>? Parameters { get; set; }

    public int? TimeoutSeconds { get; set; }

    [Description("Skip the write-confirmation prompt. Only effective when the server is started with --dangerously-skip-permissions.")]
    public bool Confirm { get; set; }
}

public sealed class ExplainArgs
{
    public required string ConnectionId { get; set; }

    public required string Sql { get; set; }

    public Dictionary<string, object?>? Parameters { get; set; }

    [Description(OutputMode.ParameterDescription)]
    public string? Output { get; set; }
}

public sealed record QueryToolResult(
    string ConnectionId,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int RowCount,
    bool Truncated,
    long ExecutionMs,
    string? ResultSetId,
    string TextTable,
    string? CsvPath,
    string? ResultUrl,
    DateTimeOffset? ResultUrlExpiresAt);

public sealed record QueryPageResult(
    string ResultSetId,
    IReadOnlyList<QueryColumn> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    int Offset,
    int NextOffset,
    long TotalRows,
    bool HasMore,
    string? ResultUrl,
    DateTimeOffset? ResultUrlExpiresAt);

public sealed record ExecuteResult(string ConnectionId, long RowsAffected, bool Executed);

public sealed record ExplainToolResult(
    string ConnectionId,
    string Format,
    string Plan,
    string? ResultUrl,
    DateTimeOffset? ResultUrlExpiresAt);
