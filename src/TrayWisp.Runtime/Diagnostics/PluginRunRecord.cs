using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Diagnostics;

/// <summary>What became of a run's output, as decided by the host after the run.</summary>
public enum RunPublication
{
    /// <summary>The run did not succeed, so it had no output to publish.</summary>
    NotApplicable,

    /// <summary>The run succeeded and a fresh trust check of its approval confirmed it; its output was shown.</summary>
    Published,

    /// <summary>The run succeeded, but its output was withheld: the trust check after the run did not confirm its approval, or could not be made.</summary>
    Withheld,
}

/// <summary>A stable diagnostic code and severity kept in a run record. Messages are never stored.</summary>
/// <param name="Code">The numeric code, for example 631 for <c>TW631</c>.</param>
/// <param name="Severity">The diagnostic's severity.</param>
public readonly record struct RecordedDiagnostic(int Code, PluginDiagnosticSeverity Severity)
{
    /// <summary>The code as written in documentation, for example <c>TW631</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"TW{Code}");
}

/// <summary>
/// A structured summary of one plugin run, as kept by <see cref="PluginRunHistory"/>. It holds no output, labels,
/// actions, standard error, script content, environment variables, interpreter paths or command lines, and no
/// diagnostic messages, only their codes. See docs/diagnostics.md ("What is stored").
/// </summary>
public sealed class PluginRunRecord
{
    internal PluginRunRecord(
        DateTimeOffset recordedAtUtc,
        DateTimeOffset? startedAtUtc,
        string rootId,
        PluginId pluginId,
        PluginExecutionStatus status,
        PluginTrustStatus? trustStatus,
        int? exitCode,
        long elapsedMilliseconds,
        ExecutionTerminationReason terminationReason,
        ProcessCleanupStatus cleanupStatus,
        bool jobObjectAssigned,
        int? remainingJobProcesses,
        long standardOutputBytes,
        bool standardOutputComplete,
        bool standardOutputTruncated,
        long standardErrorBytes,
        bool standardErrorTruncated,
        InterpreterKind? interpreterKind,
        InterpreterSource? interpreterSource,
        RunPublication publication,
        PluginTrustStatus? publicationTrustStatus,
        ImmutableArray<RecordedDiagnostic> diagnostics,
        bool diagnosticsTruncated)
    {
        RecordedAtUtc = recordedAtUtc;
        StartedAtUtc = startedAtUtc;
        RootId = rootId;
        PluginId = pluginId;
        Status = status;
        TrustStatus = trustStatus;
        ExitCode = exitCode;
        ElapsedMilliseconds = elapsedMilliseconds;
        TerminationReason = terminationReason;
        CleanupStatus = cleanupStatus;
        JobObjectAssigned = jobObjectAssigned;
        RemainingJobProcesses = remainingJobProcesses;
        StandardOutputBytes = standardOutputBytes;
        StandardOutputComplete = standardOutputComplete;
        StandardOutputTruncated = standardOutputTruncated;
        StandardErrorBytes = standardErrorBytes;
        StandardErrorTruncated = standardErrorTruncated;
        InterpreterKind = interpreterKind;
        InterpreterSource = interpreterSource;
        Publication = publication;
        PublicationTrustStatus = publicationTrustStatus;
        Diagnostics = diagnostics;
        DiagnosticsTruncated = diagnosticsTruncated;
    }

    /// <summary>When the record was written (UTC).</summary>
    public DateTimeOffset RecordedAtUtc { get; }

    /// <summary>When the plugin process was started (UTC), or <see langword="null"/> if nothing was started.</summary>
    public DateTimeOffset? StartedAtUtc { get; }

    /// <summary>
    /// 32 lowercase hexadecimal characters identifying the plugin root without storing its path: a truncated SHA-256 of
    /// the root's case-folded path. Not anonymous: anyone who knows or guesses a path can compute it.
    /// </summary>
    public string RootId { get; }

    /// <summary>The plugin's root-relative identity.</summary>
    public PluginId PluginId { get; }

    /// <summary>The run's execution status.</summary>
    public PluginExecutionStatus Status { get; }

    /// <summary>The launch-time trust status, when a trust check was made.</summary>
    public PluginTrustStatus? TrustStatus { get; }

    /// <summary>The plugin process's own exit code, when it exited. Not TrayWisp's exit code.</summary>
    public int? ExitCode { get; }

    /// <summary>The run's monotonic duration in milliseconds (0 when nothing was started).</summary>
    public long ElapsedMilliseconds { get; }

    /// <summary>Why TrayWisp ended the run, if it did.</summary>
    public ExecutionTerminationReason TerminationReason { get; }

    /// <summary>The outcome of process cleanup.</summary>
    public ProcessCleanupStatus CleanupStatus { get; }

    /// <summary>Whether the process was assigned to the run's job object.</summary>
    public bool JobObjectAssigned { get; }

    /// <summary>Processes still in the job when cleanup ended, when known.</summary>
    public int? RemainingJobProcesses { get; }

    /// <summary>Bytes of standard output captured.</summary>
    public long StandardOutputBytes { get; }

    /// <summary>Whether standard output was captured completely.</summary>
    public bool StandardOutputComplete { get; }

    /// <summary>Whether standard output exceeded its limit.</summary>
    public bool StandardOutputTruncated { get; }

    /// <summary>Bytes the plugin wrote to standard error (the text itself is never stored).</summary>
    public long StandardErrorBytes { get; }

    /// <summary>Whether the standard error tail was shortened.</summary>
    public bool StandardErrorTruncated { get; }

    /// <summary>The kind of interpreter used, when one was resolved. Its path is not stored.</summary>
    public InterpreterKind? InterpreterKind { get; }

    /// <summary>Where the interpreter was found, when one was resolved.</summary>
    public InterpreterSource? InterpreterSource { get; }

    /// <summary>What became of the output.</summary>
    public RunPublication Publication { get; }

    /// <summary>The status of the trust check made after the run, when one was made.</summary>
    public PluginTrustStatus? PublicationTrustStatus { get; }

    /// <summary>Distinct diagnostic codes of the run (execution, then trust, then output), in first-seen order.</summary>
    public ImmutableArray<RecordedDiagnostic> Diagnostics { get; }

    /// <summary>Whether more distinct codes occurred than <see cref="PluginRunHistory.MaxDiagnosticCodesPerRecord"/>.</summary>
    public bool DiagnosticsTruncated { get; }

    /// <summary>Whether this record belongs to the plugin root <paramref name="rootPath"/> (compared like <see cref="PluginRoot"/>).</summary>
    public bool IsForRoot(string rootPath) =>
        PluginRoot.TryCreate(rootPath, out PluginRoot? root) && string.Equals(ComputeRootId(root), RootId, StringComparison.Ordinal);

    // Case-folded like PluginRoot's ordinal case-insensitive equality, with a fixed domain prefix, truncated to 128 bits.
    internal static string ComputeRootId(PluginRoot root)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("TrayWisp.RootId.v1\n" + root.Value.ToUpperInvariant()));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }
}
