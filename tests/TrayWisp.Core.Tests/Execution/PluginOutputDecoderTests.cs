using System.Text;
using TrayWisp.Core.Execution;
using Xunit;

namespace TrayWisp.Core.Tests.Execution;

public sealed class PluginOutputDecoderTests
{
    private static string Decode(byte[] bytes, out StandardOutputEncoding encoding)
    {
        Assert.True(PluginOutputDecoder.TryDecodeStandardOutput(bytes, out string? text, out encoding, out string? error), error);
        return text;
    }

    private static string Failure(byte[] bytes)
    {
        Assert.False(PluginOutputDecoder.TryDecodeStandardOutput(bytes, out string? text, out _, out string? error));
        Assert.Null(text);
        return error;
    }

    [Fact]
    public void EmptyOutputIsEmptyText()
    {
        Assert.Equal(string.Empty, Decode([], out StandardOutputEncoding encoding));
        Assert.Equal(StandardOutputEncoding.Utf8, encoding);
    }

    [Fact]
    public void DecodesUtf8IncludingSupplementaryCharacters()
    {
        const string Text = "Caf\u00E9 \u65E5\u672C \uD83D\uDE80\n---\nItem";
        Assert.Equal(Text, Decode(Encoding.UTF8.GetBytes(Text), out StandardOutputEncoding encoding));
        Assert.Equal(StandardOutputEncoding.Utf8, encoding);
    }

    [Fact]
    public void RemovesAUtf8ByteOrderMark()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. "Title"u8];
        Assert.Equal("Title", Decode(bytes, out StandardOutputEncoding encoding));
        Assert.Equal(StandardOutputEncoding.Utf8WithBom, encoding);
    }

    [Fact]
    public void DecodesUtf16WithByteOrderMarks()
    {
        const string Text = "T\u00EFtle \uD83D\uDE80\n";
        byte[] little = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes(Text)];
        byte[] big = [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(Text)];

        Assert.Equal(Text, Decode(little, out StandardOutputEncoding littleEncoding));
        Assert.Equal(StandardOutputEncoding.Utf16LittleEndian, littleEncoding);
        Assert.Equal(Text, Decode(big, out StandardOutputEncoding bigEncoding));
        Assert.Equal(StandardOutputEncoding.Utf16BigEndian, bigEncoding);
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0xC3 })] // truncated two-byte sequence at the end
    [InlineData(new byte[] { 0x41, 0xE2, 0x82 })] // truncated three-byte sequence
    [InlineData(new byte[] { 0xF0, 0x9F, 0x9A })] // truncated four-byte sequence
    [InlineData(new byte[] { 0x41, 0x80, 0x42 })] // lone continuation byte
    [InlineData(new byte[] { 0xC0, 0xAF })] // overlong encoding of '/'
    [InlineData(new byte[] { 0xED, 0xA0, 0x80 })] // encoded surrogate
    [InlineData(new byte[] { 0xF4, 0x90, 0x80, 0x80 })] // beyond U+10FFFF
    [InlineData(new byte[] { 0x82, 0x41 })] // a legacy code page byte (OEM 437 '\u00E9')
    [InlineData(new byte[] { 0xFF })]
    public void RejectsMalformedUtf8InsteadOfReplacingIt(byte[] bytes)
    {
        string error = Failure(bytes);
        Assert.Contains("not valid UTF-8", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsTheOffsetOfTheInvalidSequenceButNotTheContent()
    {
        byte[] bytes = [.. "shell=editor.exe param1=doc"u8, 0xC3, .. "ument.txt"u8];
        string error = Failure(bytes);
        Assert.Contains("byte offset 27", error, StringComparison.Ordinal);
        Assert.DoesNotContain("editor", error, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsOffsetsAfterAByteOrderMark()
    {
        byte[] bytes = [0xEF, 0xBB, 0xBF, 0x41, 0xFF];
        Assert.Contains("byte offset 4", Failure(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsMalformedUtf16()
    {
        Assert.Contains("odd number", Failure([0xFF, 0xFE, 0x41, 0x00, 0x42]), StringComparison.Ordinal);
        Assert.Contains("unpaired surrogate", Failure([0xFF, 0xFE, 0x3D, 0xD8, 0x41, 0x00]), StringComparison.Ordinal);
        Assert.Contains("unpaired surrogate", Failure([0xFE, 0xFF, 0xDE, 0x80]), StringComparison.Ordinal);
    }

    [Fact]
    public void RejectsUtf32()
    {
        byte[] bytes = [0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00];
        Assert.Contains("UTF-32", Failure(bytes), StringComparison.Ordinal);
    }

    [Fact]
    public void KeepsAByteOrderMarkInTheMiddle()
    {
        byte[] bytes = [.. "A"u8, 0xEF, 0xBB, 0xBF, .. "B"u8];
        Assert.Equal("A\uFEFFB", Decode(bytes, out _));
    }

    [Fact]
    public void FormatsStandardErrorSafely()
    {
        byte[] bytes = [.. "first\r\nsec\tond\u001B[31m\n\u202Eevil"u8, 0xFF, .. "\n"u8];
        string text = PluginOutputDecoder.FormatStandardError(bytes, startsMidStream: false, out bool shortened);
        Assert.Equal("first\nsec ond\uFFFD[31m\nevil\uFFFD", text);
        Assert.False(shortened);
    }

    [Fact]
    public void StandardErrorTailDropsItsPartialFirstLine()
    {
        byte[] bytes = [0x9F, 0x9A, 0x80, .. "rest of a line\nwhole line\n"u8];
        string text = PluginOutputDecoder.FormatStandardError(bytes, startsMidStream: true, out bool shortened);
        Assert.Equal("whole line", text);
        Assert.True(shortened);
    }

    [Fact]
    public void AStandardErrorTailWithoutLineBreaksKeepsItsCompleteCharacters()
    {
        byte[] bytes = [0x9F, 0x9A, 0x80, .. "one long line"u8];
        string text = PluginOutputDecoder.FormatStandardError(bytes, startsMidStream: true, out bool shortened);
        Assert.Equal("one long line", text);
        Assert.True(shortened);
    }

    [Fact]
    public void StandardErrorIsBoundedInLinesAndLineLength()
    {
        var builder = new StringBuilder();
        for (int i = 0; i < PluginOutputDecoder.MaxStandardErrorLines + 20; i++)
        {
            builder.Append("line ").Append(i).Append('\n');
        }

        builder.Append(new string('x', PluginOutputDecoder.MaxStandardErrorLineLength + 1));
        string text = PluginOutputDecoder.FormatStandardError(Encoding.UTF8.GetBytes(builder.ToString()), startsMidStream: false, out bool shortened);
        string[] lines = text.Split('\n');

        Assert.True(shortened);
        Assert.Equal(PluginOutputDecoder.MaxStandardErrorLines, lines.Length);
        Assert.Equal(new string('x', PluginOutputDecoder.MaxStandardErrorLineLength) + "...", lines[^1]);
        Assert.Equal("line 21", lines[0]);
    }

    [Fact]
    public void StandardErrorLineCutNeverSplitsASurrogatePair()
    {
        string line = new string('x', PluginOutputDecoder.MaxStandardErrorLineLength - 1) + "\uD83D\uDE80tail";
        string text = PluginOutputDecoder.FormatStandardError(Encoding.UTF8.GetBytes(line), startsMidStream: false, out _);
        Assert.Equal(new string('x', PluginOutputDecoder.MaxStandardErrorLineLength - 1) + "...", text);
    }

    [Fact]
    public void EmptyStandardErrorIsEmpty()
    {
        Assert.Equal(string.Empty, PluginOutputDecoder.FormatStandardError([], startsMidStream: false, out bool shortened));
        Assert.False(shortened);
    }
}
