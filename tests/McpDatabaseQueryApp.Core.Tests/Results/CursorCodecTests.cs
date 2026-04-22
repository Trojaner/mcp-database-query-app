using McpDatabaseQueryApp.Core.Results;
using FluentAssertions;
using Xunit;

namespace McpDatabaseQueryApp.Core.Tests.Results;

public sealed class CursorCodecTests
{
    [Fact]
    public void Encodes_and_decodes_roundtrip()
    {
        var original = new Cursor(100, 50, "foo", "name asc");
        var encoded = CursorCodec.Encode(original);

        var decoded = CursorCodec.Decode(encoded);

        decoded.Should().Be(original);
    }

    [Fact]
    public void Encoded_cursor_is_url_safe()
    {
        var encoded = CursorCodec.Encode(new Cursor(1, 2));
        encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=");
    }

    [Fact]
    public void TryDecode_returns_false_for_null()
    {
        CursorCodec.TryDecode(null, out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_returns_false_for_garbage()
    {
        CursorCodec.TryDecode("@@@@@@", out _).Should().BeFalse();
    }

    [Fact]
    public void TryDecode_returns_true_for_valid_cursor()
    {
        var encoded = CursorCodec.Encode(new Cursor(5, 10));

        CursorCodec.TryDecode(encoded, out var cursor).Should().BeTrue();
        cursor.Offset.Should().Be(5);
        cursor.Limit.Should().Be(10);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(100, 50)]
    [InlineData(1_000_000, 500)]
    public void Handles_various_offsets(int offset, int limit)
    {
        var encoded = CursorCodec.Encode(new Cursor(offset, limit));
        var decoded = CursorCodec.Decode(encoded);
        decoded.Offset.Should().Be(offset);
        decoded.Limit.Should().Be(limit);
    }
}
