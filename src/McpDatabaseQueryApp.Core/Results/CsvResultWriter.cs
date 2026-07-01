namespace McpDatabaseQueryApp.Core.Results;

/// <summary>
/// Streams a query result set to a <see cref="TextWriter"/> as RFC 4180 CSV.
/// A row is just <c>IReadOnlyList&lt;object?&gt;</c>, so the same writer serves
/// both live query results and cached result sets, and streaming keeps memory
/// bounded when exporting large result sets to a file.
/// </summary>
public static class CsvResultWriter
{
    /// <summary>
    /// Writes a header row of <paramref name="columnNames"/> followed by one
    /// line per row. Rows are LF-terminated; nulls render as empty fields.
    /// </summary>
    public static async Task WriteAsync(
        TextWriter writer,
        IEnumerable<string> columnNames,
        IReadOnlyList<IReadOnlyList<object?>> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(columnNames);
        ArgumentNullException.ThrowIfNull(rows);

        await writer.WriteAsync(FormatRow(columnNames).AsMemory(), cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await writer.WriteAsync(FormatRow(row.Select(Stringify)).AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string FormatRow(IEnumerable<string> cells) => string.Join(',', cells.Select(Escape)) + '\n';

    private static string Stringify(object? cell) => cell?.ToString() ?? string.Empty;

    /// <summary>Quotes and escapes a single field per RFC 4180 when required.</summary>
    public static string Escape(string value)
    {
        if (value.Contains(',', StringComparison.Ordinal) ||
            value.Contains('"', StringComparison.Ordinal) ||
            value.Contains('\n', StringComparison.Ordinal) ||
            value.Contains('\r', StringComparison.Ordinal))
        {
            return '"' + value.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';
        }

        return value;
    }
}
