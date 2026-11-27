using System.Globalization;
using System.Text;

namespace TrayWisp.Core.Plugins;

/// <summary>
/// Formats untrusted text, such as file names, paths and operating-system messages, for inclusion
/// in a <see cref="PluginDiagnostic.Message"/> or in <see cref="PluginDiagnostic.ToString"/>: the text is
/// length-limited, characters that are invisible or can change how text is displayed are shown as
/// <c>\uXXXX</c> escapes, and a literal backslash is shown as <c>\\</c>, so that text which merely
/// looks like an escape cannot be mistaken for one.
/// </summary>
public static class PluginDiagnosticText
{
    /// <summary>Default maximum number of characters of the original text that are kept.</summary>
    public const int DefaultMaxLength = 64;

    /// <summary>
    /// Returns <paramref name="value"/> in single quotes, escaped and cut to at most
    /// <paramref name="maxLength"/> characters of the original (followed by <c>...</c> when cut).
    /// </summary>
    public static string Quote(ReadOnlySpan<char> value, int maxLength = DefaultMaxLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxLength);

        var builder = new StringBuilder(Math.Min(value.Length, maxLength) + 5);
        builder.Append('\'');
        int end = Math.Min(value.Length, maxLength);

        // Do not split a surrogate pair at the cut.
        if (end < value.Length && end > 0 && char.IsHighSurrogate(value[end - 1]))
        {
            end--;
        }

        for (int i = 0; i < end; i++)
        {
            char c = value[i];
            bool pairedSurrogate = (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
                || (char.IsLowSurrogate(c) && i > 0 && char.IsHighSurrogate(value[i - 1]));

            if (c == '\\')
            {
                builder.Append(@"\\");
            }
            else if (pairedSurrogate || !NeedsEscape(c))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:X4}");
            }
        }

        if (end < value.Length)
        {
            builder.Append("...");
        }

        return builder.Append('\'').ToString();
    }

    // The quote character itself is escaped so the quoted text cannot appear to end early.
    private static bool NeedsEscape(char c) => c == '\'' || CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned;
}
