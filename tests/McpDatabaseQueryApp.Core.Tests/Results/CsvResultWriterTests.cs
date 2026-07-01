using McpDatabaseQueryApp.Core.Results;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Results;

public sealed class CsvResultWriterTests
{
    private static async Task<string> WriteAsync(
        IEnumerable<string> columns,
        IReadOnlyList<IReadOnlyList<object?>> rows)
    {
        await using var stringWriter = new StringWriter();
        await CsvResultWriter.WriteAsync(stringWriter, columns, rows);
        return stringWriter.ToString();
    }

    [Fact]
    public async Task Writes_header_then_one_lf_terminated_line_per_row()
    {
        var csv = await WriteAsync(
            new[] { "id", "name" },
            new IReadOnlyList<object?>[]
            {
                new object?[] { 1, "alice" },
                new object?[] { 2, "bob" },
            });

        csv.Should().Be("id,name\n1,alice\n2,bob\n");
    }

    [Fact]
    public async Task Quotes_fields_containing_delimiters_quotes_or_newlines()
    {
        var csv = await WriteAsync(
            new[] { "name", "notes" },
            new IReadOnlyList<object?>[]
            {
                new object?[] { "alice", "hello, world" },
                new object?[] { "bob", "quote \"yes\"" },
                new object?[] { "eve", "multi\nline" },
                new object?[] { "mia", "carriage\rreturn" },
            });

        csv.Should().Contain("\"hello, world\"");
        csv.Should().Contain("\"quote \"\"yes\"\"\"");
        csv.Should().Contain("\"multi\nline\"");
        csv.Should().Contain("\"carriage\rreturn\"");
    }

    [Fact]
    public async Task Renders_null_cells_as_empty_fields()
    {
        var csv = await WriteAsync(
            new[] { "a", "b" },
            new IReadOnlyList<object?>[] { new object?[] { null, "x" } });

        csv.Should().Be("a,b\n,x\n");
    }

    [Fact]
    public async Task Writes_header_only_when_there_are_no_rows()
    {
        var csv = await WriteAsync(new[] { "a", "b" }, Array.Empty<IReadOnlyList<object?>>());

        csv.Should().Be("a,b\n");
    }
}
