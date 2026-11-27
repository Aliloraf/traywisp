using System.Buffers;

namespace TrayWisp.Core.Plugins;

// File-name rules shared by the file-name parser and PluginId. They are applied on every platform
// so that parsing does not depend on the operating system it runs on.
internal static class FileNameRules
{
    public const string InvalidCharacterDescription = "a control character or one of < > : \" / \\ | ? *";

    private static readonly SearchValues<char> InvalidCharacters = SearchValues.Create(
        "\0\u0001\u0002\u0003\u0004\u0005\u0006\u0007\b\t\n\u000B\f\r\u000E\u000F"
        + "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F"
        + "<>:\"/\\|?*");

    private static readonly SearchValues<char> BidiControls = SearchValues.Create(
        "\u061C\u200E\u200F\u202A\u202B\u202C\u202D\u202E\u2066\u2067\u2068\u2069");

    public static bool ContainsInvalidCharacter(ReadOnlySpan<char> value) => value.ContainsAny(InvalidCharacters);

    // Bidirectional controls can make a name display differently from what it is, for example
    // "report" + U+202E + "slx.ps1" renders as "reportsp1.xls". They are never needed in a file name.
    public static bool ContainsBidiControl(ReadOnlySpan<char> value) => value.ContainsAny(BidiControls);

    // Windows reserves these device names, with or without an extension, in any case.
    public static bool IsReservedDeviceName(ReadOnlySpan<char> name)
    {
        int dot = name.IndexOf('.');
        ReadOnlySpan<char> baseName = (dot >= 0 ? name[..dot] : name).TrimEnd(' ');

        if (baseName.Length == 3)
        {
            return baseName.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || baseName.Equals("NUL", StringComparison.OrdinalIgnoreCase);
        }

        if (baseName.Length == 4)
        {
            ReadOnlySpan<char> prefix = baseName[..3];
            char last = baseName[3];
            bool isPort = prefix.Equals("COM", StringComparison.OrdinalIgnoreCase)
                || prefix.Equals("LPT", StringComparison.OrdinalIgnoreCase);
            return isPort && (char.IsAsciiDigit(last) || last is '¹' or '²' or '³');
        }

        return false;
    }
}
