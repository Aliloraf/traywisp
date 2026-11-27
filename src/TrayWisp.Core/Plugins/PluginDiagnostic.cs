using System.Globalization;
using System.Text;

namespace TrayWisp.Core.Plugins;

/// <summary>How serious a <see cref="PluginDiagnostic"/> is.</summary>
public enum PluginDiagnosticSeverity
{
    /// <summary>Informational. Nothing is wrong, but behaviour may differ from what the author expects.</summary>
    Info,

    /// <summary>Something was ignored or could not be fully processed. Discovery continues.</summary>
    Warning,

    /// <summary>A candidate plugin was rejected, or the plugin root could not be scanned.</summary>
    Error,
}

/// <summary>
/// Stable identifiers for diagnostics. Numeric values are part of the documented format
/// (see docs/plugin-format.md) and must not be renumbered.
/// </summary>
public enum PluginDiagnosticCode
{
    /// <summary>The plugin name is invalid (whitespace, reserved device name, empty segment, invalid character, too long).</summary>
    InvalidName = 100,

    /// <summary>There is no plugin name before the interval or the extension.</summary>
    MissingName = 101,

    /// <summary>
    /// The segment before the extension is a number without a unit, so it was taken as part of the name
    /// and the plugin refreshes manually. Informational: the unit may have been forgotten.
    /// </summary>
    NumericNameSegment = 102,

    /// <summary>The interval segment looks like an interval but is malformed (fractional, no unit, unknown unit, non-ASCII digits, sign, whitespace).</summary>
    MalformedInterval = 110,

    /// <summary>The interval is negative.</summary>
    NegativeInterval = 111,

    /// <summary>The interval is zero.</summary>
    ZeroInterval = 112,

    /// <summary>The interval is below 250 milliseconds, above 30 days, or overflows.</summary>
    IntervalOutOfRange = 113,

    /// <summary>The metadata region is not valid UTF-8 or UTF-16.</summary>
    MetadataInvalidEncoding = 200,

    /// <summary>The metadata region uses an unsupported encoding (UTF-32) or contains NUL characters.</summary>
    MetadataUnsupportedEncoding = 201,

    /// <summary>Only the first <see cref="PluginMetadataParser.MaxScanBytes"/> bytes were scanned for metadata.</summary>
    MetadataScanTruncated = 202,

    /// <summary>A tag in a recognized namespace is malformed (no closing tag on the same line, attributes, nesting).</summary>
    MetadataMalformedTag = 210,

    /// <summary>More than <see cref="PluginMetadataParser.MaxTags"/> tags were found. Later tags were ignored.</summary>
    MetadataTagLimitReached = 211,

    /// <summary>A metadata field has an empty value and was treated as absent.</summary>
    MetadataEmptyValue = 212,

    /// <summary>A metadata value is longer than <see cref="PluginMetadataParser.MaxValueLength"/> characters and was ignored.</summary>
    MetadataValueTooLong = 213,

    /// <summary>A metadata value contains control characters and was ignored.</summary>
    MetadataInvalidValue = 214,

    /// <summary>A metadata tag outside a comment (in code or a string literal) was ignored. Reported once per file.</summary>
    MetadataTagOutsideComment = 215,

    /// <summary>A tag in a recognized namespace names a field TrayWisp does not know.</summary>
    MetadataUnknownField = 220,

    /// <summary>A known SwiftBar/xbar behavioural directive that TrayWisp does not implement.</summary>
    MetadataUnsupportedDirective = 221,

    /// <summary>The same field appears more than once in one namespace with different values.</summary>
    MetadataFieldConflict = 230,

    /// <summary>A field value is overridden by the same field in a higher-precedence namespace.</summary>
    MetadataFieldOverridden = 231,

    /// <summary>The plugin root directory does not exist.</summary>
    RootNotFound = 300,

    /// <summary>The plugin root path is not a directory.</summary>
    RootNotDirectory = 301,

    /// <summary>The plugin root is a symbolic link, junction or other reparse point.</summary>
    RootIsReparsePoint = 302,

    /// <summary>A directory could not be listed (access denied, deleted during the scan, I/O error).</summary>
    DirectoryUnreadable = 310,

    /// <summary>A symbolic link, junction or other reparse point was skipped.</summary>
    ReparsePointSkipped = 311,

    /// <summary>A directory or path name cannot form a valid plugin identity.</summary>
    InvalidPathSegment = 312,

    /// <summary>An entry marked offline or stored remotely (for example a cloud placeholder) was skipped, because reading it could trigger a download.</summary>
    RemoteStorageSkipped = 313,

    /// <summary>A candidate plugin file could not be read.</summary>
    FileUnreadable = 320,

    /// <summary>A candidate plugin file was deleted between listing and reading.</summary>
    FileDisappeared = 321,

    /// <summary>
    /// Two or more candidate files, or two or more directories, in one directory have names that differ only by
    /// case, so their plugin identities would collide. None of them is used.
    /// </summary>
    IdentityCollision = 330,

    /// <summary>A directory was not scanned because it is deeper than the configured maximum depth.</summary>
    DepthLimitReached = 340,

    /// <summary>Discovery stopped after inspecting the maximum number of directory entries.</summary>
    EntryLimitReached = 341,

    /// <summary>Discovery stopped after finding the maximum number of candidate plugins.</summary>
    CandidateLimitReached = 342,
}

/// <summary>
/// A structured message produced while parsing file names or metadata, or while discovering plugins.
/// Messages never echo metadata values. File names and paths in messages are length-limited and escaped
/// with <see cref="PluginDiagnosticText"/>. <see cref="Path"/> holds the raw path for programmatic use;
/// <see cref="ToString"/> escapes and length-limits it, and any other display must do the same.
/// </summary>
/// <param name="Severity">How serious the diagnostic is.</param>
/// <param name="Code">Stable diagnostic identifier.</param>
/// <param name="Message">Human-readable English description.</param>
public sealed record PluginDiagnostic(PluginDiagnosticSeverity Severity, PluginDiagnosticCode Code, string Message)
{
    /// <summary>Maximum number of characters of <see cref="Path"/> that <see cref="ToString"/> shows.</summary>
    public const int MaxPathLengthInText = 260;

    /// <summary>
    /// Root-relative path (with <c>/</c> separators) of the file or directory concerned, if any. This is raw,
    /// untrusted file-system text.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>One-based line number within the scanned metadata region, if any.</summary>
    public int? Line { get; init; }

    /// <summary>
    /// Formats the diagnostic as <c>Severity TWcode 'path':line: message</c>. The path is quoted, escaped
    /// and cut to <see cref="MaxPathLengthInText"/> characters with <see cref="PluginDiagnosticText.Quote"/>,
    /// so it cannot hide or reorder text or imitate the rest of the line.
    /// </summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{Severity} TW{(int)Code}");
        if (Path is not null)
        {
            builder.Append(' ').Append(PluginDiagnosticText.Quote(Path, MaxPathLengthInText));
            if (Line is int line)
            {
                builder.Append(CultureInfo.InvariantCulture, $":{line}");
            }
        }

        builder.Append(": ").Append(Message);
        return builder.ToString();
    }
}
