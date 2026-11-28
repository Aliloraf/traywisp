using System.Globalization;
using System.Text;
using TrayWisp.Core.Output;

namespace TrayWisp.Cli.Output;

// The one display policy for untrusted text in human-readable output (docs/cli.md, "Terminal safety"). Every value that
// comes from plugin files, metadata, plugin output, the file system, the environment or the command line goes through
// Inline before it is written. TrayWisp writes no colour or formatting escapes of its own.
//
// Inline produces a single line: control characters (C0, DEL, C1, including ESC, CR, LF, TAB and backspace), format
// characters (bidirectional controls, zero-width characters, tag characters), line and paragraph separators, lone
// surrogates and unassigned code points are written as \uXXXX escapes (one per UTF-16 code unit), so terminal escape
// sequences, OSC hyperlinks, carriage-return overwrites, reordering and line injection are impossible and invisible
// characters become visible. Except in labels, so are the other invisible characters: Default_Ignorable_Code_Point
// (Hangul fillers, variation selectors, the combining grapheme joiner and their supplementary-plane counterparts), U+2800
// BRAILLE PATTERN BLANK and every space other than U+0020 (UnicodeVisibility.IsDefaultIgnorableOrBlank, the Phase 2 list). Other
// text, including non-ASCII letters, emoji and spaces, is kept.
//
// Identities and paths (Kind.Identity) are also unambiguous: a backslash directly followed by 'u' is written as \u005C,
// so every "\u" in an identity starts an escape; a space at either end is written as \u0020; and they are not shortened
// unless a caller asks for it (their own types already bound their length). In other text, backslashes are kept as they
// are, so a value can look as if it contained an escape, but can never hide a character.
internal static class TerminalText
{
    public const int DefaultMaxLength = 1024;

    public const string TruncationMarker = "...";

    public enum Kind
    {
        // Plugin identities, roots and other paths: every invisible character escaped, literal escapes and edge spaces
        // disambiguated, not shortened by default.
        Identity,

        // TrayWisp's messages and other untrusted text: every invisible character escaped.
        Text,

        // Display labels (titles, menu text, metadata): keeps U+200D (zero-width joiner), which emoji sequences need, and
        // the other default-ignorable characters that are not format characters, such as emoji variation selectors.
        Label,
    }

    // The length limit a field of this kind gets when its caller names none.
    public static int DefaultLimit(Kind kind) => kind == Kind.Identity ? int.MaxValue : DefaultMaxLength;

    public static string Inline(string? value, int maxLength = DefaultMaxLength, Kind kind = Kind.Text)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(value.Length, maxLength) + 8);
        for (int i = 0; i < value.Length; i++)
        {
            if (builder.Length >= maxLength)
            {
                return builder.Append(TruncationMarker).ToString();
            }

            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                var rune = new Rune(c, value[i + 1]);
                if (IsUnsafe(Rune.GetUnicodeCategory(rune), rune.Value, kind))
                {
                    Escape(builder, c);
                    Escape(builder, value[i + 1]);
                }
                else
                {
                    builder.Append(c).Append(value[i + 1]);
                }

                i++;
                continue;
            }

            if (char.IsSurrogate(c) || IsUnsafe(CharUnicodeInfo.GetUnicodeCategory(c), c, kind) || (kind == Kind.Identity && IsAmbiguous(value, i)))
            {
                Escape(builder, c);
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    // Text with line structure (a plugin's standard error tail): each line is made safe on its own.
    public static IEnumerable<string> Lines(string? value, int maxLines, int maxLineLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield break;
        }

        string[] lines = value.Split('\n');
        int start = Math.Max(0, lines.Length - maxLines);
        for (int i = start; i < lines.Length; i++)
        {
            yield return Inline(lines[i].TrimEnd('\r'), maxLineLength, Kind.Label);
        }
    }

    // Whether the text contains characters outside ASCII, which could be lookalikes of other characters.
    public static bool HasNonAscii(string value) => !value.All(char.IsAscii);

    private static bool IsUnsafe(UnicodeCategory category, int codePoint, Kind kind) => category switch
    {
        UnicodeCategory.Control or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator
            or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned => true,
        UnicodeCategory.Format => !(kind == Kind.Label && codePoint == 0x200D),
        _ when kind == Kind.Label => false,
        UnicodeCategory.SpaceSeparator => codePoint != ' ',
        _ => UnicodeVisibility.IsDefaultIgnorableOrBlank(codePoint),
    };

    // In an identity: a backslash that would read as the start of an escape, or a space at either end.
    private static bool IsAmbiguous(string value, int index) => value[index] switch
    {
        '\\' => index + 1 < value.Length && value[index + 1] == 'u',
        ' ' => index == 0 || index == value.Length - 1,
        _ => false,
    };

    private static void Escape(StringBuilder builder, char c) => builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
}
