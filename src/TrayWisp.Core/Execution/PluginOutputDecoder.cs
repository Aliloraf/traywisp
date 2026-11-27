using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Unicode;
using TrayWisp.Core.Output;
using TrayWisp.Core.Trust;

namespace TrayWisp.Core.Execution;

/// <summary>The encoding a plugin's standard output was decoded with.</summary>
public enum StandardOutputEncoding
{
    /// <summary>UTF-8 without a byte order mark (the default).</summary>
    Utf8,

    /// <summary>UTF-8 with a leading byte order mark (<c>EF BB BF</c>), which was removed.</summary>
    Utf8WithBom,

    /// <summary>UTF-16 little endian, recognized only by its byte order mark (<c>FF FE</c>), which was removed.</summary>
    Utf16LittleEndian,

    /// <summary>UTF-16 big endian, recognized only by its byte order mark (<c>FE FF</c>), which was removed.</summary>
    Utf16BigEndian,
}

/// <summary>
/// Turns captured plugin output bytes into text. Pure functions, see docs/execution-engine.md ("Output encoding").
/// </summary>
/// <remarks>
/// <para>Standard output is decoded strictly: UTF-8, or UTF-16 when the output starts with a UTF-16 byte order mark.
/// Malformed or truncated sequences are never replaced, so they cannot turn into different text in a menu label,
/// a command argument or a URL. Code pages are never guessed.</para>
/// <para>Standard error is diagnostic text, never action input, so it is decoded on a best-effort basis and made
/// safe to display.</para>
/// </remarks>
public static class PluginOutputDecoder
{
    /// <summary>Most lines kept from the end of standard error by <see cref="FormatStandardError"/>.</summary>
    public const int MaxStandardErrorLines = 100;

    /// <summary>Longest standard error line kept by <see cref="FormatStandardError"/>, in UTF-16 code units, before <c>...</c>.</summary>
    public const int MaxStandardErrorLineLength = 500;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Decodes complete standard output. A UTF-8 byte order mark selects UTF-8, <c>FF FE</c> UTF-16 little endian and
    /// <c>FE FF</c> UTF-16 big endian (the mark is removed); anything else must be UTF-8. <c>FF FE 00 00</c> (UTF-32)
    /// is not supported. Decoding is strict: invalid or incomplete sequences, unpaired surrogates and an odd number
    /// of UTF-16 bytes fail, with a message that gives the byte offset but never the content.
    /// </summary>
    public static bool TryDecodeStandardOutput(ReadOnlySpan<byte> bytes, [NotNullWhen(true)] out string? text, out StandardOutputEncoding encoding, [NotNullWhen(false)] out string? error)
    {
        text = null;
        error = null;
        if (bytes.StartsWith(Utf8Bom))
        {
            encoding = StandardOutputEncoding.Utf8WithBom;
            return TryDecodeUtf8(bytes[Utf8Bom.Length..], Utf8Bom.Length, out text, out error);
        }

        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
        {
            encoding = StandardOutputEncoding.Utf8;
            error = "The output starts with a UTF-32 byte order mark; UTF-32 is not supported.";
            return false;
        }

        if (bytes.Length >= 2 && ((bytes[0] == 0xFF && bytes[1] == 0xFE) || (bytes[0] == 0xFE && bytes[1] == 0xFF)))
        {
            bool littleEndian = bytes[0] == 0xFF;
            encoding = littleEndian ? StandardOutputEncoding.Utf16LittleEndian : StandardOutputEncoding.Utf16BigEndian;
            return TryDecodeUtf16(bytes[2..], littleEndian, out text, out error);
        }

        encoding = StandardOutputEncoding.Utf8;
        return TryDecodeUtf8(bytes, 0, out text, out error);
    }

    /// <summary>
    /// Formats retained standard error for display in diagnostics: best-effort UTF-8 (invalid sequences become
    /// U+FFFD, a leading byte order mark is removed), CRLF and LF line breaks, control characters, line separators
    /// and lone surrogates replaced with U+FFFD, tabs shown as spaces and directional formatting characters removed
    /// (as for menu labels). At most <see cref="MaxStandardErrorLines"/> lines from the end are kept, each cut to
    /// <see cref="MaxStandardErrorLineLength"/> code units.
    /// </summary>
    /// <param name="bytes">The retained bytes.</param>
    /// <param name="startsMidStream">
    /// <see langword="true"/> when <paramref name="bytes"/> is the end of a longer stream. The first, partial line is
    /// then dropped if a later line follows; otherwise only a partial UTF-8 sequence at the start is dropped.
    /// </param>
    /// <param name="shortened"><see langword="true"/> when lines or characters of <paramref name="bytes"/> were left out.</param>
    public static string FormatStandardError(ReadOnlySpan<byte> bytes, bool startsMidStream, out bool shortened)
    {
        shortened = false;
        if (startsMidStream)
        {
            // Skip the partial first line, which also skips a partial UTF-8 sequence at the start. Without any line
            // break, keep the line and skip only the continuation bytes of a partial sequence.
            shortened = true;
            int newline = bytes.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                bytes = bytes[(newline + 1)..];
            }
            else
            {
                int skip = 0;
                while (skip < 3 && skip < bytes.Length && (bytes[skip] & 0xC0) == 0x80)
                {
                    skip++;
                }

                bytes = bytes[skip..];
            }
        }
        else if (bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }

        string decoded = Encoding.UTF8.GetString(bytes);
        var lines = new List<string>();
        int start = 0;
        while (start < decoded.Length)
        {
            int newline = decoded.IndexOf('\n', start);
            int end = newline < 0 ? decoded.Length : newline;
            ReadOnlySpan<char> line = decoded.AsSpan(start, end - start);
            if (newline >= 0 && line.EndsWith('\r'))
            {
                line = line[..^1];
            }

            lines.Add(FormatLine(line, ref shortened));
            start = end + 1;
        }

        if (lines.Count > MaxStandardErrorLines)
        {
            lines.RemoveRange(0, lines.Count - MaxStandardErrorLines);
            shortened = true;
        }

        return string.Join('\n', lines);
    }

    private static string FormatLine(ReadOnlySpan<char> line, ref bool shortened)
    {
        string display = OutputText.ToDisplayText(line, unescapePipes: false, out _);
        if (display.Length <= MaxStandardErrorLineLength)
        {
            return display;
        }

        int end = MaxStandardErrorLineLength;
        if (char.IsHighSurrogate(display[end - 1]))
        {
            end--;
        }

        shortened = true;
        return string.Concat(display.AsSpan(0, end), "...");
    }

    private static bool TryDecodeUtf8(ReadOnlySpan<byte> bytes, int offset, [NotNullWhen(true)] out string? text, [NotNullWhen(false)] out string? error)
    {
        text = null;
        error = null;
        char[] buffer = ArrayPool<char>.Shared.Rent(Math.Max(bytes.Length, 1));
        try
        {
            OperationStatus status = Utf8.ToUtf16(bytes, buffer, out int read, out int written, replaceInvalidSequences: false, isFinalBlock: true);
            if (status != OperationStatus.Done)
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"The output is not valid UTF-8: an invalid or incomplete byte sequence starts at byte offset {offset + read}.");
                return false;
            }

            text = new string(buffer, 0, written);
            return true;
        }
        finally
        {
            ArrayPool<char>.Shared.Return(buffer);
        }
    }

    private static bool TryDecodeUtf16(ReadOnlySpan<byte> bytes, bool littleEndian, [NotNullWhen(true)] out string? text, [NotNullWhen(false)] out string? error)
    {
        text = null;
        error = null;
        if (bytes.Length % 2 != 0)
        {
            error = "The output starts with a UTF-16 byte order mark but has an odd number of bytes, so its last character is incomplete.";
            return false;
        }

        char[] chars = new char[bytes.Length / 2];
        for (int i = 0; i < chars.Length; i++)
        {
            ReadOnlySpan<byte> unit = bytes.Slice(i * 2, 2);
            chars[i] = (char)(littleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(unit) : BinaryPrimitives.ReadUInt16BigEndian(unit));
        }

        if (!Utf16Text.IsWellFormed(chars))
        {
            error = "The output starts with a UTF-16 byte order mark but contains an unpaired surrogate.";
            return false;
        }

        text = new string(chars);
        return true;
    }
}
