using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Output;

/// <summary>
/// Stable identifiers for output-parsing diagnostics, shown as <c>TW400</c>–<c>TW499</c>. The range is
/// separate from <see cref="PluginDiagnosticCode"/> (<c>TW100</c>–<c>TW399</c>). Numeric values are part of the
/// documented protocol (see docs/output-protocol.md) and must not be renumbered.
/// </summary>
public enum OutputDiagnosticCode
{
    /// <summary>The output is longer than <see cref="PluginOutputParser.MaxInputLength"/>. Fatal.</summary>
    OutputTooLong = 400,

    /// <summary>The output has more than <see cref="PluginOutputParser.MaxLines"/> lines. Fatal.</summary>
    TooManyLines = 401,

    /// <summary>The output has more than <see cref="PluginOutputParser.MaxTitles"/> header titles. Fatal.</summary>
    TooManyTitles = 402,

    /// <summary>The menu has more than <see cref="PluginOutputParser.MaxMenuNodes"/> items and separators. Fatal.</summary>
    TooManyMenuNodes = 403,

    /// <summary>More than <see cref="PluginOutputParser.MaxDiagnostics"/> diagnostics were produced; the rest were not recorded.</summary>
    DiagnosticLimitReached = 404,

    /// <summary>The caller reported that the output was truncated (it is only a prefix of what the plugin wrote). Fatal.</summary>
    OutputTruncated = 405,

    /// <summary>A physical line, blank or not, is longer than <see cref="PluginOutputParser.MaxLineLength"/>. Fatal.</summary>
    LineTooLong = 410,

    /// <summary>A menu item or separator is nested deeper than <see cref="PluginOutputParser.MaxDepth"/>. It was dropped with its children.</summary>
    MenuDepthExceeded = 411,

    /// <summary>A nested menu item or separator has no parent at the level above it. It was dropped with its children.</summary>
    OrphanedMenuNode = 412,

    /// <summary>A line has a label that shows nothing (empty, white space or format characters only). It was dropped with its parameters and children.</summary>
    EmptyLabel = 413,

    /// <summary>A line consisting only of an even number of hyphens is not a separator. It was dropped.</summary>
    MalformedSeparator = 414,

    /// <summary>A menu item with a submenu also declared an action. The action was removed and the submenu kept.</summary>
    ParentActionRemoved = 415,

    /// <summary>A SwiftBar streaming delimiter (a line that is exactly <c>~~~</c>) was found. Streaming is not supported. Fatal.</summary>
    StreamingNotSupported = 416,

    /// <summary>The output looks like JSON. Only the text format is supported; it was parsed as text.</summary>
    JsonNotSupported = 417,

    /// <summary>Control characters, line separators, invalid UTF-16 or directional formatting characters in display text were replaced or removed.</summary>
    UnsafeText = 420,

    /// <summary>The parameter section of a line is malformed. All of its parameters were ignored.</summary>
    MalformedParameters = 430,

    /// <summary>A line has more than <see cref="PluginOutputParser.MaxParametersPerLine"/> parameters. All of its parameters were ignored.</summary>
    TooManyParameters = 431,

    /// <summary>A parameter appears more than once on a line. It was treated as invalid.</summary>
    DuplicateParameter = 432,

    /// <summary>A parameter value is invalid, out of range or too long.</summary>
    InvalidParameterValue = 433,

    /// <summary>A parameter name is not recognized.</summary>
    UnknownParameter = 434,

    /// <summary>A known SwiftBar/xbar appearance parameter that TrayWisp does not support. It was ignored.</summary>
    UnsupportedParameter = 435,

    /// <summary>A known SwiftBar/xbar behavioural parameter that TrayWisp does not support. Any action on the line was suppressed.</summary>
    UnsupportedBehavior = 436,

    /// <summary>A parameter is valid but does not apply where it was used (for example <c>dropdown</c> on a menu item).</summary>
    ParameterNotApplicable = 437,

    /// <summary>An <c>href</c> value is not an acceptable absolute HTTP or HTTPS URL. No action was created.</summary>
    InvalidUrl = 440,

    /// <summary>A <c>shell</c>/<c>bash</c> executable value is empty or contains unsafe characters. No action was created.</summary>
    InvalidCommand = 441,

    /// <summary>The numbered command arguments are malformed, not contiguous, duplicated or too many. No action was created.</summary>
    InvalidCommandArguments = 442,

    /// <summary>A line declares both a URL and a command. No action was created.</summary>
    ConflictingActions = 443,

    /// <summary>Command arguments or a terminal preference appear without a command. No action was created.</summary>
    OrphanedActionParameter = 444,

    /// <summary>A disabled item declared an action. The action was not exposed.</summary>
    DisabledItemAction = 445,
}

/// <summary>
/// A structured message produced while parsing plugin output. Messages are written by the parser and never
/// repeat labels, parameter values or command arguments. Untrusted text that does appear, such as an unknown
/// parameter name, is length-limited and escaped with <see cref="PluginDiagnosticText.Quote"/>.
/// </summary>
/// <param name="Severity">How serious the diagnostic is.</param>
/// <param name="Code">Stable diagnostic identifier.</param>
/// <param name="Message">Human-readable English description.</param>
public sealed record OutputDiagnostic(PluginDiagnosticSeverity Severity, OutputDiagnosticCode Code, string Message)
{
    /// <summary>One-based physical line number in the output, if the diagnostic concerns a line.</summary>
    public int? Line { get; init; }

    /// <summary>One-based column (in UTF-16 code units) within <see cref="Line"/>, if known.</summary>
    public int? Column { get; init; }

    /// <summary>Formats the diagnostic as <c>Severity TWcode line:column: message</c>.</summary>
    public override string ToString()
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{Severity} TW{(int)Code}");
        if (Line is int line)
        {
            builder.Append(CultureInfo.InvariantCulture, $" {line}");
            if (Column is int column)
            {
                builder.Append(CultureInfo.InvariantCulture, $":{column}");
            }
        }

        builder.Append(": ").Append(Message);
        return builder.ToString();
    }
}

/// <summary>
/// Collects diagnostics up to <see cref="PluginOutputParser.MaxDiagnostics"/>. Two entries are reserved, one
/// for a fatal error and one for the summary of suppressed diagnostics, so both always fit. Whether a
/// diagnostic is recorded never influences parsing decisions.
/// </summary>
internal sealed class OutputDiagnosticBag
{
    private readonly List<OutputDiagnostic> _items = [];
    private int _suppressed;

    public void Add(PluginDiagnosticSeverity severity, OutputDiagnosticCode code, string message, int? line, int? column = null)
    {
        if (_items.Count < PluginOutputParser.MaxDiagnostics - 2)
        {
            _items.Add(new OutputDiagnostic(severity, code, message) { Line = line, Column = column });
        }
        else
        {
            _suppressed++;
        }
    }

    /// <summary>Returns the recorded diagnostics, then <paramref name="fatal"/>, then the suppression summary if any.</summary>
    public ImmutableArray<OutputDiagnostic> ToImmutable(OutputDiagnostic? fatal, out bool truncated)
    {
        var builder = ImmutableArray.CreateBuilder<OutputDiagnostic>(_items.Count + 2);
        builder.AddRange(_items);
        if (fatal is not null)
        {
            builder.Add(fatal);
        }

        truncated = _suppressed > 0;
        if (truncated)
        {
            builder.Add(new OutputDiagnostic(
                PluginDiagnosticSeverity.Warning,
                OutputDiagnosticCode.DiagnosticLimitReached,
                $"{_suppressed.ToString(CultureInfo.InvariantCulture)} further diagnostics were not recorded (limit {PluginOutputParser.MaxDiagnostics})."));
        }

        return builder.ToImmutable();
    }
}
