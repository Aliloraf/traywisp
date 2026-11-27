namespace TrayWisp.Core.Execution;

/// <summary>
/// The outcome of one plugin run. Exactly one status describes a run; when several problems occur, the
/// precedence documented in docs/execution-engine.md ("Result semantics") decides which one is reported.
/// Only <see cref="Succeeded"/> carries publishable output.
/// </summary>
public enum PluginExecutionStatus
{
    /// <summary>The process exited with code 0, both output streams were captured completely, and the output was decoded and parsed.</summary>
    Succeeded,

    /// <summary>Plugin execution is not supported on this platform. Nothing was started.</summary>
    UnsupportedPlatform,

    /// <summary>The plugin's language has no execution support. Nothing was started.</summary>
    UnsupportedLanguage,

    /// <summary>The explicitly configured interpreter is missing or unsafe. Nothing was started, and no other interpreter was tried.</summary>
    InterpreterConfigurationInvalid,

    /// <summary>No acceptable interpreter was found. Nothing was started.</summary>
    InterpreterUnavailable,

    /// <summary>The authoritative trust check did not approve the plugin. Nothing was started.</summary>
    TrustDenied,

    /// <summary>The caller cancelled the run. If the process had started, it was terminated.</summary>
    Cancelled,

    /// <summary>The interpreter process could not be created.</summary>
    ProcessStartFailed,

    /// <summary>The run did not complete within its timeout and was terminated.</summary>
    TimedOut,

    /// <summary>The plugin wrote more standard output than the limit and was terminated.</summary>
    OutputLimitExceeded,

    /// <summary>Reading an output stream failed. The process was terminated.</summary>
    OutputCaptureFailed,

    /// <summary>The process exited with a nonzero exit code.</summary>
    NonZeroExitCode,

    /// <summary>The process exited with code 0, but its standard output is not valid text in a supported encoding.</summary>
    OutputDecodingFailed,

    /// <summary>The process exited with code 0, but its output could not be parsed as a whole.</summary>
    OutputParsingFailed,

    /// <summary>
    /// TrayWisp could not confirm within the cleanup period that the process exited, that its output streams closed,
    /// or, with a job object, that every tracked process exited. This applies both to a terminated run and to the
    /// cleanup of tracked processes left behind by a process that exited by itself. Processes started for the plugin
    /// may still be running, and no output is published.
    /// </summary>
    CleanupIncomplete,

    /// <summary>
    /// Supervision of the started process could not be set up or maintained (its output streams, their capture or the
    /// monitoring of its exit failed). The process was terminated and its output rejected.
    /// </summary>
    SupervisionFailed,
}

/// <summary>Why TrayWisp stopped a running plugin process.</summary>
public enum ExecutionTerminationReason
{
    /// <summary>TrayWisp did not stop the process: it was not started, or it exited by itself.</summary>
    None,

    /// <summary>The run timeout elapsed before the process exited and both output streams ended.</summary>
    Timeout,

    /// <summary>The caller cancelled the run.</summary>
    Cancellation,

    /// <summary>The standard output exceeded the capture limit.</summary>
    OutputLimit,

    /// <summary>Reading an output stream failed.</summary>
    CaptureFailure,

    /// <summary>Supervision of the process could not be set up or maintained.</summary>
    SupervisionFailure,
}

/// <summary>What became of the plugin's process at the end of a run.</summary>
public enum ProcessCleanupStatus
{
    /// <summary>No process was started.</summary>
    NotStarted,

    /// <summary>
    /// The process exited by itself and both output streams ended. TrayWisp terminated nothing: with a job object, no
    /// tracked process remained. Without one, descendants that released the output streams are untracked and
    /// unconfirmed.
    /// </summary>
    Exited,

    /// <summary>
    /// TrayWisp terminated the run, and confirmed that the process exited, that both output streams closed and, with
    /// a job object, that the job became empty.
    /// </summary>
    Terminated,

    /// <summary>
    /// TrayWisp could not confirm within the cleanup period that the process exited, that both output streams closed,
    /// or that every tracked process exited (including when the job object could not be queried or terminated).
    /// </summary>
    Incomplete,

    /// <summary>
    /// The process exited by itself and both output streams ended, but tracked processes it started were still in the
    /// job object. TrayWisp terminated them and confirmed that the job became empty. The interpreter itself was not
    /// terminated.
    /// </summary>
    DescendantsTerminated,
}

/// <summary>The interpreter family used to run a plugin.</summary>
public enum InterpreterKind
{
    /// <summary>PowerShell 7 or later (<c>pwsh.exe</c>).</summary>
    PowerShell,

    /// <summary>Windows PowerShell 5.1 (<c>powershell.exe</c>).</summary>
    WindowsPowerShell,

    /// <summary>Python 3 (<c>python.exe</c> or <c>python3.exe</c>).</summary>
    Python,

    /// <summary>Node.js (<c>node.exe</c>).</summary>
    Node,
}

/// <summary>Where an interpreter path came from.</summary>
public enum InterpreterSource
{
    /// <summary>The host configured the interpreter's full path explicitly.</summary>
    Configured,

    /// <summary>A fixed installation location (PowerShell 7 under Program Files).</summary>
    KnownLocation,

    /// <summary>A directory listed in the <c>PATH</c> environment variable of the TrayWisp process.</summary>
    SearchPath,

    /// <summary>The Windows system directory (Windows PowerShell).</summary>
    SystemDirectory,
}
