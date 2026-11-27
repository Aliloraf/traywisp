using System.Globalization;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Execution;

/// <summary>
/// Stable identifiers for plugin execution diagnostics, shown as <c>TW600</c>–<c>TW699</c>. The range is
/// separate from plugin (<c>TW100</c>–<c>TW399</c>), output (<c>TW400</c>–<c>TW499</c>) and trust
/// (<c>TW500</c>–<c>TW599</c>) diagnostics. Numeric values are part of the documented format (see
/// docs/execution-engine.md) and must not be renumbered.
/// </summary>
public enum ExecutionDiagnosticCode
{
    /// <summary>Plugin execution is only supported on Windows.</summary>
    UnsupportedPlatform = 600,

    /// <summary>The plugin's language has no execution support (Bash, batch).</summary>
    UnsupportedLanguage = 601,

    /// <summary>No acceptable interpreter for the plugin's language was found.</summary>
    InterpreterNotFound = 610,

    /// <summary>An interpreter candidate was found but rejected (execution alias, link, remote, inside the plugin root, not a regular file).</summary>
    InterpreterCandidateRejected = 611,

    /// <summary>The explicitly configured interpreter is missing or unsafe. TrayWisp does not fall back to another interpreter.</summary>
    ConfiguredInterpreterInvalid = 612,

    /// <summary>Some <c>PATH</c> entries were ignored (empty, relative, remote, malformed, unsafe, inside the plugin root or beyond the limits).</summary>
    PathEntriesIgnored = 613,

    /// <summary>The plugin was not authorized to run. The trust diagnostics say why.</summary>
    TrustDenied = 620,

    /// <summary>The validated plugin file does not have the language of the plugin identity.</summary>
    LanguageMismatch = 621,

    /// <summary>The run was cancelled by the caller, before or after the process started.</summary>
    Cancelled = 630,

    /// <summary>The interpreter process could not be started.</summary>
    ProcessStartFailed = 631,

    /// <summary>The plugin process exited with a nonzero exit code.</summary>
    NonZeroExitCode = 632,

    /// <summary>The run did not complete (process exit and end of both output streams) within its timeout.</summary>
    TimedOut = 633,

    /// <summary>
    /// Supervision of the started process could not be set up or maintained (its output streams, their capture or the
    /// monitoring of its exit failed), so the run was terminated and its output rejected.
    /// </summary>
    SupervisionFailed = 634,

    /// <summary>The plugin wrote more standard output than the capture limit.</summary>
    OutputLimitExceeded = 640,

    /// <summary>The plugin wrote more standard error than is retained; only the end was kept.</summary>
    StandardErrorTruncated = 641,

    /// <summary>Reading the plugin's standard output or standard error failed.</summary>
    OutputCaptureFailed = 642,

    /// <summary>The standard output is not valid text in a supported encoding.</summary>
    OutputDecodingFailed = 643,

    /// <summary>The standard output could not be parsed as a whole. The output diagnostics say why.</summary>
    OutputParsingFailed = 644,

    /// <summary>
    /// TrayWisp could not confirm, within the cleanup period, that the plugin's process exited, that its output streams
    /// closed, or that every process in its job object exited. Processes started for the plugin may still be running.
    /// </summary>
    CleanupIncomplete = 650,

    /// <summary>The plugin process could not be added to a Windows job object, so only best-effort process-tree termination is available for it.</summary>
    JobObjectUnavailable = 651,

    /// <summary>
    /// Processes in the run's job object, started by the plugin, were still running after the interpreter exited by
    /// itself. A run owns its tracked processes, so TrayWisp terminated them; the diagnostic says whether that was
    /// confirmed. The interpreter itself was not terminated.
    /// </summary>
    DescendantsTerminated = 652,

    /// <summary>More diagnostics were produced than one result may hold. Later ones were dropped.</summary>
    DiagnosticLimitReached = 699,
}

/// <summary>
/// A structured message produced while running a plugin. Messages never contain environment variable values,
/// script content or captured output. Paths and system messages in them are length-limited and escaped with
/// <see cref="PluginDiagnosticText"/>.
/// </summary>
/// <param name="Severity">How serious the diagnostic is.</param>
/// <param name="Code">Stable diagnostic identifier.</param>
/// <param name="Message">Human-readable English description.</param>
public sealed record ExecutionDiagnostic(PluginDiagnosticSeverity Severity, ExecutionDiagnosticCode Code, string Message)
{
    /// <summary>Formats the diagnostic as <c>Severity TWcode: message</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Severity} TW{(int)Code}: {Message}");
}
