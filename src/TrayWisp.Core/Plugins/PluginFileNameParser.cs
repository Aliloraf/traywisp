using System.Diagnostics.CodeAnalysis;

namespace TrayWisp.Core.Plugins;

/// <summary>Outcome of <see cref="PluginFileNameParser.Parse"/>.</summary>
public enum PluginFileNameParseStatus
{
    /// <summary>The extension is not a recognized plugin language. The file is not a plugin and is ignored silently.</summary>
    NotCandidate,

    /// <summary>The file name is a valid plugin file name.</summary>
    Parsed,

    /// <summary>The extension is recognized but the rest of the file name is invalid. See <see cref="PluginFileNameParseResult.Diagnostic"/>.</summary>
    Rejected,
}

/// <summary>Result of parsing a plugin file name.</summary>
public sealed class PluginFileNameParseResult
{
    private PluginFileNameParseResult(PluginFileNameParseStatus status, PluginFileName? fileName, PluginDiagnostic? diagnostic)
    {
        Status = status;
        FileName = fileName;
        Diagnostic = diagnostic;
    }

    /// <summary>Whether the file is a valid plugin, a rejected candidate, or not a candidate at all.</summary>
    public PluginFileNameParseStatus Status { get; }

    /// <summary>The parsed file name when <see cref="Status"/> is <see cref="PluginFileNameParseStatus.Parsed"/>.</summary>
    public PluginFileName? FileName { get; }

    /// <summary>
    /// The error when <see cref="Status"/> is <see cref="PluginFileNameParseStatus.Rejected"/>. For a parsed
    /// file name, an optional informational note (<see cref="PluginDiagnosticCode.NumericNameSegment"/>).
    /// </summary>
    public PluginDiagnostic? Diagnostic { get; }

    /// <summary><see langword="true"/> when <see cref="FileName"/> is available.</summary>
    [MemberNotNullWhen(true, nameof(FileName))]
    public bool IsParsed => Status == PluginFileNameParseStatus.Parsed;

    internal static PluginFileNameParseResult NotCandidate { get; } = new(PluginFileNameParseStatus.NotCandidate, null, null);

    internal static PluginFileNameParseResult Parsed(PluginFileName fileName, PluginDiagnostic? note) =>
        new(PluginFileNameParseStatus.Parsed, fileName, note);

    internal static PluginFileNameParseResult Rejected(PluginDiagnosticCode code, string message) =>
        new(PluginFileNameParseStatus.Rejected, null, new PluginDiagnostic(PluginDiagnosticSeverity.Error, code, message));
}

/// <summary>
/// Parses plugin file names of the form <c>name[.interval].extension</c>. The grammar is documented in
/// docs/plugin-format.md. Parsing is culture-independent and never throws.
/// </summary>
public static class PluginFileNameParser
{
    /// <summary>Longest accepted file name, in UTF-16 code units (the usual Windows component limit).</summary>
    public const int MaxFileNameLength = 255;

    // The largest number that can be valid with any unit (30 days in milliseconds). Accumulation
    // stops once a number exceeds it, so arbitrarily long digit strings cannot overflow.
    private const long LargestUsefulNumber = 30L * 24 * 60 * 60 * 1000;

    private const string UnitList = "ms, s, m, h or d";

    /// <summary>Parses a file name (not a path).</summary>
    public static PluginFileNameParseResult Parse(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return PluginFileNameParseResult.NotCandidate;
        }

        // 1. The extension is the text after the last dot. Unknown extensions are not candidates.
        int extensionDot = fileName.LastIndexOf('.');
        string extension = extensionDot < 0 ? string.Empty : fileName[(extensionDot + 1)..];
        if (!PluginLanguages.TryClassifyExtension(extension, out PluginLanguage language))
        {
            return PluginFileNameParseResult.NotCandidate;
        }

        // From here on the file is a candidate, so every problem is reported.
        if (fileName.Length > MaxFileNameLength)
        {
            return Reject(PluginDiagnosticCode.InvalidName, $"The file name is longer than {MaxFileNameLength} characters.");
        }

        if (FileNameRules.ContainsInvalidCharacter(fileName))
        {
            return Reject(PluginDiagnosticCode.InvalidName, $"The file name contains {FileNameRules.InvalidCharacterDescription}.");
        }

        if (FileNameRules.ContainsBidiControl(fileName))
        {
            return Reject(
                PluginDiagnosticCode.InvalidName,
                "The file name contains a Unicode bidirectional control character, which can make it display as a different name or extension.");
        }

        string stem = fileName[..extensionDot];
        string name = stem;
        RefreshInterval? interval = null;
        PluginDiagnostic? note = null;

        int intervalDot = stem.LastIndexOf('.');
        if (intervalDot < 0)
        {
            // 2a. A single segment is always the name, unless it is nothing but an interval ("5s.ps1").
            if (IsPlainIntervalSyntax(stem))
            {
                return Reject(PluginDiagnosticCode.MissingName, $"The file name has the interval {Quote(stem)} but no plugin name before it.");
            }
        }
        else
        {
            // 2b. The last segment before the extension is an interval if it is interval-shaped.
            string segment = stem[(intervalDot + 1)..];
            if (IsIntervalAttempt(segment))
            {
                name = stem[..intervalDot];

                // 3. "clock.1.5s" is a fractional interval, not the name "clock.1" with interval "5s".
                string previous = name[(name.LastIndexOf('.') + 1)..];
                if (IsIntegerText(previous))
                {
                    return Reject(
                        PluginDiagnosticCode.MalformedInterval,
                        $"{Quote(previous + "." + segment)} looks like a fractional interval. Intervals must be whole numbers; "
                        + "a name segment that is only digits cannot directly precede the interval.");
                }

                if (!TryParseInterval(segment, out interval, out PluginDiagnosticCode code, out string? message))
                {
                    return Reject(code, message);
                }
            }
            else if (IsDigitsOnly(segment))
            {
                // A number without a unit is a name segment ("backup.2024"), but say so in case a unit was forgotten.
                note = new PluginDiagnostic(
                    PluginDiagnosticSeverity.Info,
                    PluginDiagnosticCode.NumericNameSegment,
                    $"{Quote(segment)} has no unit, so it is part of the plugin name and the plugin refreshes manually. "
                    + $"Add a unit ({UnitList}) if a refresh interval was intended.");
            }
        }

        // 4. Validate the name.
        if (ValidateName(name) is (PluginDiagnosticCode nameCode, string nameMessage))
        {
            return Reject(nameCode, nameMessage);
        }

        return PluginFileNameParseResult.Parsed(new PluginFileName(fileName, name, interval, extension, language), note);
    }

    // An interval attempt starts with an optional sign and a decimal digit (any script) and contains
    // only letters, digits, '_' and ','. A segment of digits alone is not an attempt (it has no unit
    // and is common in names, such as years and versions), unless it has a sign. Segments that are
    // attempts after trimming whitespace are attempts too, so that " 5s" is reported, not accepted.
    private static bool IsIntervalAttempt(ReadOnlySpan<char> segment)
    {
        segment = segment.Trim();
        bool signed = segment.Length > 0 && segment[0] is '+' or '-';
        int i = signed ? 1 : 0;
        if (i >= segment.Length || !char.IsDigit(segment[i]))
        {
            return false;
        }

        bool hasNonDigit = signed;
        for (; i < segment.Length; i++)
        {
            char c = segment[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != ',')
            {
                return false;
            }

            hasNonDigit |= !char.IsDigit(c);
        }

        return hasNonDigit;
    }

    private static bool TryParseInterval(
        string segment,
        [NotNullWhen(true)] out RefreshInterval? interval,
        out PluginDiagnosticCode code,
        [NotNullWhen(false)] out string? message)
    {
        interval = null;
        code = PluginDiagnosticCode.MalformedInterval;
        message = null;

        if (segment.Trim().Length != segment.Length)
        {
            message = $"The interval {Quote(segment.Trim())} must not be surrounded by whitespace.";
            return false;
        }

        if (segment[0] == '-')
        {
            code = PluginDiagnosticCode.NegativeInterval;
            message = $"The interval {Quote(segment)} is negative. Intervals must be positive.";
            return false;
        }

        if (segment[0] == '+')
        {
            message = $"The interval {Quote(segment)} must not have a sign.";
            return false;
        }

        int digitCount = 0;
        while (digitCount < segment.Length && char.IsDigit(segment[digitCount]))
        {
            digitCount++;
        }

        ReadOnlySpan<char> digits = segment.AsSpan(0, digitCount);
        ReadOnlySpan<char> unitText = segment.AsSpan(digitCount);

        if (digits.ContainsAnyExceptInRange('0', '9'))
        {
            message = $"The interval {Quote(segment)} must use the ASCII digits 0-9.";
            return false;
        }

        if (unitText.IsEmpty)
        {
            message = $"The interval {Quote(segment)} has no unit. Expected {UnitList}.";
            return false;
        }

        if (!TryParseUnit(unitText, out RefreshIntervalUnit unit))
        {
            message = $"The interval {Quote(segment)} has an unrecognized unit {Quote(unitText)}. Expected a whole number followed by {UnitList}.";
            return false;
        }

        long value = 0;
        foreach (char digit in digits)
        {
            value = (value * 10) + (digit - '0');
            if (value > LargestUsefulNumber)
            {
                code = PluginDiagnosticCode.IntervalOutOfRange;
                message = $"The interval {Quote(segment)} is too large. The maximum is 30 days.";
                return false;
            }
        }

        if (value == 0)
        {
            code = PluginDiagnosticCode.ZeroInterval;
            message = $"The interval {Quote(segment)} is zero. Omit the interval for manual refresh.";
            return false;
        }

        if (!RefreshInterval.TryCreate(value, unit, out interval))
        {
            code = PluginDiagnosticCode.IntervalOutOfRange;
            message = $"The interval {Quote(segment)} is outside the allowed range of 250ms to 30d.";
            return false;
        }

        return true;
    }

    // ASCII digits followed by a recognized unit, e.g. "5s" or "0ms".
    private static bool IsPlainIntervalSyntax(ReadOnlySpan<char> segment)
    {
        int digitCount = 0;
        while (digitCount < segment.Length && char.IsAsciiDigit(segment[digitCount]))
        {
            digitCount++;
        }

        return digitCount > 0 && TryParseUnit(segment[digitCount..], out _);
    }

    private static bool TryParseUnit(ReadOnlySpan<char> text, out RefreshIntervalUnit unit)
    {
        (bool known, unit) = text switch
        {
            _ when text.Equals("ms", StringComparison.OrdinalIgnoreCase) => (true, RefreshIntervalUnit.Milliseconds),
            _ when text.Equals("s", StringComparison.OrdinalIgnoreCase) => (true, RefreshIntervalUnit.Seconds),
            _ when text.Equals("m", StringComparison.OrdinalIgnoreCase) => (true, RefreshIntervalUnit.Minutes),
            _ when text.Equals("h", StringComparison.OrdinalIgnoreCase) => (true, RefreshIntervalUnit.Hours),
            _ when text.Equals("d", StringComparison.OrdinalIgnoreCase) => (true, RefreshIntervalUnit.Days),
            _ => (false, default(RefreshIntervalUnit)),
        };
        return known;
    }

    // An optionally signed run of decimal digits (any script), such as "1" in "clock.1.5s".
    private static bool IsIntegerText(ReadOnlySpan<char> text) =>
        IsDigitsOnly(text.Length > 0 && text[0] is '+' or '-' ? text[1..] : text);

    private static bool IsDigitsOnly(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static (PluginDiagnosticCode Code, string Message)? ValidateName(string name)
    {
        if (name.Length == 0)
        {
            return (PluginDiagnosticCode.MissingName, "The file name has no plugin name before the interval or extension.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return (PluginDiagnosticCode.InvalidName, "The plugin name consists only of whitespace.");
        }

        if (char.IsWhiteSpace(name[0]) || char.IsWhiteSpace(name[^1]))
        {
            return (PluginDiagnosticCode.InvalidName, "The plugin name must not begin or end with whitespace.");
        }

        if (name[0] == '.' || name[^1] == '.' || name.Contains("..", StringComparison.Ordinal))
        {
            return (PluginDiagnosticCode.InvalidName, "The plugin name contains an empty dot-separated segment.");
        }

        if (FileNameRules.IsReservedDeviceName(name))
        {
            return (PluginDiagnosticCode.InvalidName, "The plugin name begins with a reserved Windows device name such as CON, NUL or COM1.");
        }

        return null;
    }

    private static string Quote(ReadOnlySpan<char> text) => PluginDiagnosticText.Quote(text, 32);

    private static PluginFileNameParseResult Reject(PluginDiagnosticCode code, string message) =>
        PluginFileNameParseResult.Rejected(code, message);
}
