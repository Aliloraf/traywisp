using System.Globalization;
using System.Text;

namespace TrayWisp.Core.Output;

/// <summary>Character policies for display text and for action values. See docs/output-protocol.md.</summary>
internal static class OutputText
{
    /// <summary>U+FFFD, which replaces unsafe characters in display text.</summary>
    public const char ReplacementCharacter = '\uFFFD';

    /// <summary>
    /// Directional formatting characters that open or close a scope: embeddings, overrides and isolates
    /// (U+202A–U+202E, U+2066–U+2069). Unbalanced, they can reorder text that follows them.
    /// </summary>
    public static bool IsDirectionalFormatting(char c) => c is (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069');

    /// <summary>Every Unicode bidirectional control: the directional formatting characters plus ALM, LRM and RLM.</summary>
    public static bool IsBidiControl(char c) => IsDirectionalFormatting(c) || c is '\u061C' or '\u200E' or '\u200F';

    /// <summary>
    /// Converts label or tooltip text into display text. A tab becomes a space. Other control characters
    /// (C0, DEL, C1, including a lone CR and ESC), U+2028, U+2029 and lone surrogates become U+FFFD.
    /// Directional formatting characters are removed. Everything else, including ZWJ emoji sequences,
    /// combining marks and the ALM/LRM/RLM marks, is kept. With <paramref name="unescapePipes"/>, <c>\|</c>
    /// becomes <c>|</c>.
    /// </summary>
    /// <param name="firstUnsafe">Index in <paramref name="text"/> of the first replaced or removed character, or -1.</param>
    public static string ToDisplayText(ReadOnlySpan<char> text, bool unescapePipes, out int firstUnsafe)
    {
        firstUnsafe = -1;
        var builder = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (unescapePipes && c == '\\' && i + 1 < text.Length && text[i + 1] == '|')
            {
                builder.Append('|');
                i++;
            }
            else if (c == '\t')
            {
                builder.Append(' ');
            }
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                builder.Append(c).Append(text[i + 1]);
                i++;
            }
            else if (char.IsSurrogate(c) || char.IsControl(c) || c is '\u2028' or '\u2029')
            {
                builder.Append(ReplacementCharacter);
                firstUnsafe = firstUnsafe < 0 ? i : firstUnsafe;
            }
            else if (IsDirectionalFormatting(c))
            {
                firstUnsafe = firstUnsafe < 0 ? i : firstUnsafe;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// True if <paramref name="value"/> is acceptable in an action value or font name: no control characters
    /// (including tab), no U+2028/U+2029, no lone surrogates and no bidirectional controls.
    /// </summary>
    public static bool IsSafeValue(ReadOnlySpan<char> value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                i++;
                continue;
            }

            if (char.IsSurrogate(c) || char.IsControl(c) || c is '\u2028' or '\u2029' || IsBidiControl(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>True if every character is white space (<see cref="char.IsWhiteSpace(char)"/>), including an empty span.</summary>
    public static bool IsBlank(ReadOnlySpan<char> text) => text.IsWhiteSpace();

    /// <summary>
    /// True if the text shows nothing: every character is white space, a format character (Unicode category
    /// Cf, such as zero-width spaces and joiners, directional marks, soft hyphens and tag characters), a
    /// Default_Ignorable_Code_Point (which adds the Hangul fillers U+115F, U+1160, U+3164 and U+FFA0, the
    /// variation selectors U+FE00–U+FE0F and U+E0100–U+E01EF, the Mongolian free variation selectors and the
    /// combining grapheme joiner) or U+2800 BRAILLE PATTERN BLANK. These characters are only used to decide
    /// whether a whole label or tooltip is invisible; they are never removed from visible text. This is a
    /// best-effort check: it cannot guarantee that every font renders every accepted label visibly. Lone
    /// surrogates count as visible; display text has already replaced them.
    /// </summary>
    public static bool IsInvisible(ReadOnlySpan<char> text)
    {
        foreach (Rune rune in text.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && Rune.GetUnicodeCategory(rune) != UnicodeCategory.Format && !IsDefaultIgnorableOrBlank(rune.Value))
            {
                return false;
            }
        }

        return true;
    }

    // Default_Ignorable_Code_Point (Unicode 16.0 DerivedCoreProperties.txt) plus U+2800 BRAILLE PATTERN BLANK,
    // which is not default-ignorable but renders as an empty cell. Also published as UnicodeVisibility.IsDefaultIgnorableOrBlank.
    internal static bool IsDefaultIgnorableOrBlank(int c) => c is 0x00AD or 0x034F or 0x061C or (>= 0x115F and <= 0x1160) or (>= 0x17B4 and <= 0x17B5)
        or (>= 0x180B and <= 0x180F) or (>= 0x200B and <= 0x200F) or (>= 0x202A and <= 0x202E) or (>= 0x2060 and <= 0x206F)
        or 0x2800 or 0x3164 or (>= 0xFE00 and <= 0xFE0F) or 0xFEFF or 0xFFA0 or (>= 0xFFF0 and <= 0xFFF8)
        or (>= 0x1BCA0 and <= 0x1BCA3) or (>= 0x1D173 and <= 0x1D17A) or (>= 0xE0000 and <= 0xE0FFF);
}
