using System.Collections.Immutable;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Execution;

/// <summary>The interpreter executable a run used or would have used.</summary>
public sealed class PluginInterpreter
{
    internal PluginInterpreter(string path, InterpreterKind kind, InterpreterSource source)
    {
        Path = path;
        Kind = kind;
        Source = source;
    }

    /// <summary>
    /// The fully qualified local path of the executable, as validated (its final path, with its on-disk spelling).
    /// Validation is not authentication: this names the file that is launched, not proof of what it is.
    /// </summary>
    public string Path { get; }

    /// <summary>The interpreter family, which decides the fixed arguments.</summary>
    public InterpreterKind Kind { get; }

    /// <summary>Where the path came from.</summary>
    public InterpreterSource Source { get; }
}

/// <summary>What became of a run's process, see docs/execution-engine.md ("Process cleanup").</summary>
public sealed class PluginProcessCleanup
{
    internal static PluginProcessCleanup NotStarted { get; } = new(ProcessCleanupStatus.NotStarted, rootExitConfirmed: false, jobObjectAssigned: false, remainingJobProcesses: null);

    internal PluginProcessCleanup(ProcessCleanupStatus status, bool rootExitConfirmed, bool jobObjectAssigned, int? remainingJobProcesses)
    {
        Status = status;
        RootExitConfirmed = rootExitConfirmed;
        JobObjectAssigned = jobObjectAssigned;
        RemainingJobProcesses = remainingJobProcesses;
    }

    /// <summary>The overall cleanup outcome.</summary>
    public ProcessCleanupStatus Status { get; }

    /// <summary>
    /// <see langword="true"/> when TrayWisp observed the interpreter process itself (the root of the plugin's process
    /// tree) exit. Says nothing about processes it started.
    /// </summary>
    public bool RootExitConfirmed { get; }

    /// <summary>
    /// <see langword="true"/> when the process was added to a Windows job object after it started. Processes it
    /// created after that were tracked, and terminated when the run was terminated or when they outlived the
    /// process. Processes it created before the assignment, and processes started outside its process tree, are not
    /// covered. When <see langword="false"/>, descendants that released the output streams are untracked: nothing is
    /// confirmed about them.
    /// </summary>
    public bool JobObjectAssigned { get; }

    /// <summary>
    /// How many processes the job object still reported when the run ended, or <see langword="null"/> without a job
    /// object or when the job object could not be queried. A run never leaves tracked processes running on purpose:
    /// after a normal exit they are terminated (<see cref="ProcessCleanupStatus.DescendantsTerminated"/>), and any
    /// process still reported, or an unknown count with a job object, makes the cleanup
    /// <see cref="ProcessCleanupStatus.Incomplete"/>.
    /// </summary>
    public int? RemainingJobProcesses { get; }
}

/// <summary>
/// The immutable result of one <see cref="PluginExecutionService.RunAsync"/> call. It describes one run. It is not
/// authorization for anything: a later action on its output must be checked again with
/// <see cref="PluginTrustService.CheckTrust(PluginApprovalIdentity, CancellationToken)"/> using <see cref="Approval"/>.
/// </summary>
public sealed class PluginExecutionResult
{
    internal PluginExecutionResult(PluginExecutionStatus status, PluginId pluginId, ResultDetails details)
    {
        Status = status;
        PluginId = pluginId;
        Key = details.Key;
        Language = details.Language;
        Approval = details.Approval;
        ValidatedFilePath = details.ValidatedFilePath;
        Interpreter = details.Interpreter;
        TrustStatus = details.TrustStatus;
        StartedAtUtc = details.StartedAtUtc;
        Elapsed = details.Elapsed;
        ExitCode = details.ExitCode;
        TerminationReason = details.TerminationReason;
        StandardOutputBytes = details.StandardOutputBytes;
        StandardOutputComplete = details.StandardOutputComplete;
        StandardOutputTruncated = details.StandardOutputTruncated;
        StandardOutputEncoding = details.StandardOutputEncoding;
        StandardErrorBytes = details.StandardErrorBytes;
        StandardErrorTruncated = details.StandardErrorTruncated;
        StandardError = details.StandardError;
        Cleanup = details.Cleanup;
        Diagnostics = details.Diagnostics;
        TrustDiagnostics = details.TrustDiagnostics;
        OutputDiagnostics = details.OutputDiagnostics;

        // Only a successful, authorized run publishes output, and always together with the approval that authorized it.
        Output = status == PluginExecutionStatus.Succeeded && Approval is not null ? details.Output : null;
        if (status == PluginExecutionStatus.Succeeded && Output is null)
        {
            throw new ArgumentException("A successful run must have an approval and output.", nameof(details));
        }
    }

    /// <summary>The outcome. Only <see cref="PluginExecutionStatus.Succeeded"/> has <see cref="Output"/>.</summary>
    public PluginExecutionStatus Status { get; }

    /// <summary><see langword="true"/> exactly when <see cref="Status"/> is <see cref="PluginExecutionStatus.Succeeded"/>.</summary>
    public bool Succeeded => Status == PluginExecutionStatus.Succeeded;

    /// <summary>The requested plugin identity.</summary>
    public PluginId PluginId { get; }

    /// <summary>The validated root and identity, when the trust check got that far.</summary>
    public PluginTrustKey? Key { get; }

    /// <summary>The plugin's language, when its file name could be parsed.</summary>
    public PluginLanguage? Language { get; }

    /// <summary>
    /// The approval identity that authorized this run, from the trust check made immediately before the launch;
    /// <see langword="null"/> when the plugin was not authorized. <see cref="Output"/> belongs to this identity.
    /// </summary>
    public PluginApprovalIdentity? Approval { get; }

    /// <summary>The exact file that was validated, hashed and launched, when the plugin was authorized.</summary>
    public string? ValidatedFilePath { get; }

    /// <summary>The resolved interpreter, when resolution succeeded.</summary>
    public PluginInterpreter? Interpreter { get; }

    /// <summary>The status of the authoritative trust check, when it was made.</summary>
    public PluginTrustStatus? TrustStatus { get; }

    /// <summary>When the process started (UTC, informational), or <see langword="null"/> when no process started.</summary>
    public DateTimeOffset? StartedAtUtc { get; }

    /// <summary>
    /// Monotonic time from immediately before the process was created until the end of its cleanup. Zero when no
    /// process was started.
    /// </summary>
    public TimeSpan Elapsed { get; }

    /// <summary>The exit code, when the process's exit was observed (including after termination).</summary>
    public int? ExitCode { get; }

    /// <summary>Why TrayWisp stopped the process, or <see cref="ExecutionTerminationReason.None"/>.</summary>
    public ExecutionTerminationReason TerminationReason { get; }

    /// <summary><see langword="true"/> when the run was stopped because its timeout elapsed.</summary>
    public bool TimedOut => TerminationReason == ExecutionTerminationReason.Timeout;

    /// <summary><see langword="true"/> when the caller cancelled the run, before or after the process started.</summary>
    public bool Cancelled => Status == PluginExecutionStatus.Cancelled || TerminationReason == ExecutionTerminationReason.Cancellation;

    /// <summary>Bytes read from standard output, counting at most one byte beyond the limit.</summary>
    public long StandardOutputBytes { get; }

    /// <summary>
    /// <see langword="true"/> when standard output was read until it ended, within the limit, before the run ended.
    /// Only complete output is decoded and parsed as complete input.
    /// </summary>
    public bool StandardOutputComplete { get; }

    /// <summary><see langword="true"/> when the plugin wrote more than <see cref="PluginExecutionService.MaxStandardOutputBytes"/>.</summary>
    public bool StandardOutputTruncated { get; }

    /// <summary>The encoding the standard output was decoded with, when decoding succeeded.</summary>
    public StandardOutputEncoding? StandardOutputEncoding { get; }

    /// <summary>Bytes read from standard error.</summary>
    public long StandardErrorBytes { get; }

    /// <summary><see langword="true"/> when <see cref="StandardError"/> does not contain everything the plugin wrote to standard error.</summary>
    public bool StandardErrorTruncated { get; }

    /// <summary>
    /// The end of the plugin's standard error, bounded and made safe to display (see
    /// <see cref="PluginOutputDecoder.FormatStandardError"/>). It may contain sensitive information the plugin printed.
    /// Standard error is diagnostic text only, and never makes a run fail by itself.
    /// </summary>
    public string StandardError { get; }

    /// <summary>What became of the process.</summary>
    public PluginProcessCleanup Cleanup { get; }

    /// <summary>
    /// The parsed output, only when <see cref="Status"/> is <see cref="PluginExecutionStatus.Succeeded"/>. Its actions
    /// are inert requests; activating one requires a new trust check with <see cref="Approval"/>.
    /// </summary>
    public PluginOutput? Output { get; }

    /// <summary>Execution diagnostics <c>TW600</c>–<c>TW699</c>, at most <see cref="PluginExecutionService.MaxDiagnostics"/>.</summary>
    public ImmutableArray<ExecutionDiagnostic> Diagnostics { get; }

    /// <summary>The trust check's own diagnostics (<c>TW500</c>–<c>TW599</c>), unchanged.</summary>
    public ImmutableArray<TrustDiagnostic> TrustDiagnostics { get; }

    /// <summary>The output parser's diagnostics (<c>TW400</c>–<c>TW499</c>), unchanged. Empty when the output was not parsed.</summary>
    public ImmutableArray<OutputDiagnostic> OutputDiagnostics { get; }
}

// Everything a result is built from, collected while a run progresses.
internal sealed class ResultDetails
{
    public PluginTrustKey? Key { get; set; }

    public PluginLanguage? Language { get; set; }

    // The approval and validated path of the lease that authorized the launch; never of any other check.
    public PluginApprovalIdentity? Approval { get; set; }

    public string? ValidatedFilePath { get; set; }

    // The run's lease, disposed when the run ends.
    public IDisposable? Lease { get; set; }

    public PluginInterpreter? Interpreter { get; set; }

    public PluginTrustStatus? TrustStatus { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public TimeSpan Elapsed { get; set; }

    public int? ExitCode { get; set; }

    public ExecutionTerminationReason TerminationReason { get; set; }

    public long StandardOutputBytes { get; set; }

    public bool StandardOutputComplete { get; set; }

    public bool StandardOutputTruncated { get; set; }

    public StandardOutputEncoding? StandardOutputEncoding { get; set; }

    public long StandardErrorBytes { get; set; }

    public bool StandardErrorTruncated { get; set; }

    public string StandardError { get; set; } = string.Empty;

    public PluginProcessCleanup Cleanup { get; set; } = PluginProcessCleanup.NotStarted;

    public PluginOutput? Output { get; set; }

    public ImmutableArray<ExecutionDiagnostic> Diagnostics { get; set; } = [];

    public ImmutableArray<TrustDiagnostic> TrustDiagnostics { get; set; } = [];

    public ImmutableArray<OutputDiagnostic> OutputDiagnostics { get; set; } = [];
}
