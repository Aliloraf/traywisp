using System.Globalization;
using System.Text.Json;
using TrayWisp.Cli.CommandLine;
using TrayWisp.Cli.Output;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Diagnostics;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Cli.Commands;

// traywisp run: exactly one plugin, once, through PluginExecutionService.RunAsync. No scheduler, no other plugin, no
// retries, no execution of output actions. Output is shown only after a fresh CheckTrust(result.Approval) on the same
// trust service confirms the approval the run was authorized under.
//
// Two trust checks follow a successful run (docs/cli.md, "run"):
//  1. Right after the run. Its decision is what the run history records ("publication when recorded"). A denial or a
//     cancellation here is final: the output is withheld and the second check is not made.
//  2. After the run history has been written (which can wait up to its lock timeout), as the last step before the report
//     is built and written. Only this check decides whether the output is shown, so no earlier decision outlives the
//     history I/O. The history is not updated with it: a record never proves that its output was displayed.
// No approval-store lock is held during the history write, and the two stores are independent: there is no transaction.
internal static class RunCommand
{
    public const int MaxStandardErrorLines = PluginOutputDecoder.MaxStandardErrorLines;

    public static async Task<CommandResult> RunAsync(CliRequest request, CliEnvironment environment, CancellationToken cancellationToken)
    {
        var result = new CommandResult();
        CliPaths? paths = CommandSupport.ResolvePaths(request, environment, result);
        if (paths is null
            || !CommandSupport.CheckStorageOverlap(paths, result)
            || !CommandSupport.ValidateRoot(paths, result, cancellationToken)
            || !CommandSupport.CheckStorageDirectory(paths.TrustDirectory, "trust store", result, cancellationToken))
        {
            return result;
        }

        PluginTrustService? trust = CommandSupport.CreateTrustService(paths, environment, result);
        if (trust is null)
        {
            return result;
        }

        var options = new PluginExecutionOptions { PowerShellPath = request.PowerShellPath, PythonPath = request.PythonPath, NodePath = request.NodePath };
        PluginExecutionService execution = environment.CreateExecutionService(trust, options);
        PluginId id = request.PluginId!;

        // Cancellation is reported by the engine as a result after its cleanup has ended; it is not thrown.
        PluginExecutionResult run = await execution.RunAsync(paths.Root.Value, id, new PluginRunOptions { Timeout = request.Timeout }, cancellationToken).ConfigureAwait(false);

        // The run's own outcome is kept separately from the decision to show its output.
        TrustCheck? recordedCheck = run.Succeeded ? TrustCheck.Make(trust, run.Approval!, cancellationToken) : null;
        RunPublication recordedPublication = Publication(recordedCheck);

        // Recorded whatever happened, also after cancellation: the history wait is bounded by its own lock timeout. A
        // failure is reported, never changes the run's result and never exposes withheld output.
        RunHistoryWriteResult? history = Record(environment, paths, run, recordedPublication, recordedCheck?.Decision?.Status, result);
        bool recorded = history?.Succeeded == true;

        // The final, authoritative check: after the history I/O, immediately before the report is built. Another
        // invocation may have revoked or replaced the approval, changed the script or made the store unavailable
        // meanwhile, or Ctrl+C may have arrived. Nothing slow follows it: the report is rendered in memory and written once.
        TrustCheck? finalCheck = recordedCheck is { Approved: true } ? TrustCheck.Make(trust, run.Approval!, cancellationToken) : recordedCheck;
        bool deniedByFinalCheck = recordedCheck is { Approved: true } && finalCheck is { Approved: false };
        RunPublication publication = Publication(finalCheck);
        PluginTrustDecision? afterRun = finalCheck?.Decision;
        bool cancelledAfterRun = finalCheck?.Cancelled == true;
        PluginOutput? shown = publication == RunPublication.Published ? run.Output : null;

        result.AddRange(run.Diagnostics.Select(CliDiagnostic.From));
        result.AddRange(CliDiagnostic.From(run.TrustDiagnostics));
        result.AddRange(run.OutputDiagnostics.Select(CliDiagnostic.From));
        if (afterRun is not null && !afterRun.IsApproved)
        {
            result.AddRange(CliDiagnostic.From(afterRun.Diagnostics));
        }

        string checkName = deniedByFinalCheck ? "the final trust check, made after the run history was written," : "the trust check after the run";
        if (publication == RunPublication.Withheld)
        {
            result.Add(CliDiagnostic.Error(
                CliDiagnosticCode.OutputWithheld,
                cancelledAfterRun
                    ? $"The run succeeded, but its output is not shown: {checkName} was cancelled."
                    : $"The run succeeded, but its output is not shown: {checkName} returned {afterRun!.Status}, so the approval it ran under is no longer confirmed."));
        }

        bool cleanupIncomplete = run.Status == PluginExecutionStatus.CleanupIncomplete || run.Cleanup.Status == ProcessCleanupStatus.Incomplete;
        if (cleanupIncomplete)
        {
            result.Add(CliDiagnostic.Error(
                CliDiagnosticCode.CleanupIncomplete,
                "Process cleanup could not be confirmed: processes started by the plugin may still be running. Check for them (for example in Task Manager) before running it again."));
        }

        (result.ExitCode, result.Summary) = ExitCode(run, publication, afterRun, cancelledAfterRun, cleanupIncomplete);

        bool showStderr = request.ShowStandardError;
        result.Json = writer =>
        {
            writer.WriteStartObject();
            writer.SafeString("root", paths.Root.Value);
            writer.SafeString("pluginId", id.Value);
            WriteExecutionJson(writer, run, showStderr);
            writer.WriteStartObject("publication");
            writer.WriteString("status", publication.ToString());
            writer.NullableEnum("trustStatus", afterRun?.Status);
            writer.WriteBoolean("trustCheckCancelled", cancelledAfterRun);
            writer.WriteBoolean("withheldByFinalCheck", deniedByFinalCheck);
            writer.WriteEndObject();
            writer.WritePropertyName("output");
            if (shown is null)
            {
                writer.WriteNullValue();
            }
            else
            {
                OutputRenderer.WriteJson(writer, shown);
            }

            writer.WriteStartObject("history");
            writer.WriteBoolean("recorded", recorded);
            writer.WriteString("status", history?.Status.ToString() ?? nameof(RunHistoryWriteStatus.Unavailable));
            writer.WriteNumber("evictedRecords", history?.EvictedRecords ?? 0);

            // What the record says about the output: the decision of the check right after the run, not of the final
            // check, and never proof that the output was displayed. Null when nothing was recorded.
            writer.WritePropertyName("recordedPublication");
            if (recorded)
            {
                writer.WriteStringValue(recordedPublication.ToString());
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.NullableEnum("recordedPublicationTrustStatus", recorded ? recordedCheck?.Decision?.Status : null);
            writer.WriteEndObject();
            writer.WriteEndObject();
        };
        result.Human = writer =>
        {
            writer.Field("Plugin", id.Value);
            writer.Field("Plugin root", paths.Root.Value);
            writer.Value("Execution", run.Status.ToString());
            if (run.TrustStatus is PluginTrustStatus trustStatus)
            {
                writer.Value("Launch-time trust check", trustStatus.ToString());
            }

            if (cleanupIncomplete)
            {
                writer.Line("WARNING: PROCESS CLEANUP INCOMPLETE. Processes started by the plugin may still be running.");
            }

            WriteExecutionHuman(writer, run, showStderr);
            writer.Line();
            switch (publication)
            {
                case RunPublication.Published:
                    writer.Line("Output (confirmed by a trust check after the run; actions are inert):");
                    using (writer.Indent())
                    {
                        OutputRenderer.WriteHuman(writer, shown!);
                    }

                    break;
                case RunPublication.Withheld:
                    string check = deniedByFinalCheck ? "The final trust check, after the run history was written," : "The trust check after the run";
                    writer.Line(cancelledAfterRun
                        ? $"Output: WITHHELD. {check} was cancelled."
                        : $"Output: WITHHELD. {check} returned {afterRun!.Status}.");
                    break;
                default:
                    writer.Line("Output: none (only a successful run's output is shown).");
                    break;
            }

            writer.Value("Run history", recorded
                ? $"recorded (a summary without output; output decision when recorded: {recordedPublication}, which does not show whether output was displayed)"
                : $"NOT recorded ({history?.Status ?? RunHistoryWriteStatus.Unavailable}); the run's result above is unaffected");
        };
        return result;
    }

    // Exit code precedence: incomplete cleanup, then cancellation, then the run's status, then the publication decision.
    // A history failure never changes it.
    private static (int Code, string? Summary) ExitCode(PluginExecutionResult run, RunPublication publication, PluginTrustDecision? afterRun, bool cancelledAfterRun, bool cleanupIncomplete)
    {
        if (cleanupIncomplete)
        {
            return (ExitCodes.CleanupIncomplete, "process cleanup could not be confirmed; processes started by the plugin may still be running");
        }

        if (run.Status == PluginExecutionStatus.Cancelled || cancelledAfterRun)
        {
            return (ExitCodes.Cancelled, "the run was cancelled");
        }

        return run.Status switch
        {
            PluginExecutionStatus.Succeeded when publication == RunPublication.Published => (ExitCodes.Success, null),
            PluginExecutionStatus.Succeeded when afterRun?.Status == PluginTrustStatus.StoreUnavailable =>
                (ExitCodes.StorageFailed, "the plugin ran, but its output is withheld: the trust store was unavailable for the check after the run"),
            PluginExecutionStatus.Succeeded => (ExitCodes.ApprovalDenied, "the plugin ran, but its output is withheld: its approval was no longer confirmed after the run"),
            PluginExecutionStatus.TrustDenied => run.TrustStatus switch
            {
                PluginTrustStatus.PendingApproval or PluginTrustStatus.ContentChanged or PluginTrustStatus.StaleApproval =>
                    (ExitCodes.ApprovalDenied, $"the plugin was not started: the trust check returned {run.TrustStatus}. Inspect and approve it first"),
                PluginTrustStatus.StoreUnavailable => (ExitCodes.StorageFailed, "the plugin was not started: the trust store is unavailable"),
                _ => (ExitCodes.PluginUnavailable, $"the plugin was not started: the trust check returned {run.TrustStatus?.ToString() ?? "a denial"}"),
            },
            PluginExecutionStatus.UnsupportedPlatform or PluginExecutionStatus.UnsupportedLanguage =>
                (ExitCodes.PluginUnavailable, $"the plugin was not started: {run.Status}"),
            PluginExecutionStatus.InterpreterConfigurationInvalid or PluginExecutionStatus.InterpreterUnavailable =>
                (ExitCodes.Configuration, $"the plugin was not started: {run.Status} (see the diagnostics)"),
            PluginExecutionStatus.TimedOut => (ExitCodes.TimedOut, "the plugin timed out and was terminated"),
            PluginExecutionStatus.OutputDecodingFailed or PluginExecutionStatus.OutputParsingFailed =>
                (ExitCodes.OutputInvalid, $"the plugin's output was rejected: {run.Status}"),
            _ => (ExitCodes.ExecutionFailed, $"the run failed: {run.Status}"),
        };
    }

    private static RunPublication Publication(TrustCheck? check) => check switch
    {
        null => RunPublication.NotApplicable,
        { Approved: true } => RunPublication.Published,
        _ => RunPublication.Withheld,
    };

    // Never throws and never changes the result's exit code: the run has already happened.
    private static RunHistoryWriteResult? Record(CliEnvironment environment, CliPaths paths, PluginExecutionResult run, RunPublication publication, PluginTrustStatus? afterRun, CommandResult result)
    {
        RunHistoryWriteResult history;
        try
        {
            history = environment.CreateRunHistory(paths.DiagnosticsDirectory.Value).Record(paths.Root.Value, run, publication, afterRun, CancellationToken.None);
        }
#pragma warning disable CA1031 // No history problem may conceal the run's result.
        catch (Exception exception) when (exception is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            result.Add(CliDiagnostic.Warning(
                CliDiagnosticCode.RunNotRecorded,
                $"The run was not added to the run history: an unexpected {exception.GetType().Name} occurred. This does not change the run's result."));
            return null;
        }

        if (!history.Succeeded)
        {
            result.AddRange(history.Diagnostics.Select(CliDiagnostic.From));
            result.Add(CliDiagnostic.Warning(
                CliDiagnosticCode.RunNotRecorded,
                "The run was not added to the run history (see the storage diagnostic). This does not change the run's result."));
        }

        return history;
    }

    private static void WriteExecutionJson(Utf8JsonWriter writer, PluginExecutionResult run, bool showStderr)
    {
        writer.WriteStartObject("execution");
        writer.WriteString("status", run.Status.ToString());
        writer.WriteBoolean("succeeded", run.Succeeded);
        writer.NullableEnum("trustStatus", run.TrustStatus);
        writer.NullableNumber("pluginExitCode", run.ExitCode);
        writer.Timestamp("startedAtUtc", run.StartedAtUtc);
        writer.WriteNumber("elapsedMilliseconds", (long)run.Elapsed.TotalMilliseconds);
        writer.WriteString("terminationReason", run.TerminationReason.ToString());
        writer.WriteBoolean("timedOut", run.TimedOut);
        writer.WriteBoolean("cancelled", run.Cancelled);
        writer.WritePropertyName("interpreter");
        if (run.Interpreter is PluginInterpreter interpreter)
        {
            writer.WriteStartObject();
            writer.SafeString("path", interpreter.Path);
            writer.WriteString("kind", interpreter.Kind.ToString());
            writer.WriteString("source", interpreter.Source.ToString());
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.WritePropertyName("approval");
        if (run.Approval is PluginApprovalIdentity approval)
        {
            writer.WriteStartObject();
            writer.WriteString("sha256", approval.Fingerprint.Value);
            writer.WriteString("generation", approval.Generation.Value);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteNullValue();
        }

        writer.SafeString("validatedFilePath", run.ValidatedFilePath);
        writer.WriteStartObject("stdout");
        writer.WriteNumber("bytes", run.StandardOutputBytes);
        writer.WriteBoolean("complete", run.StandardOutputComplete);
        writer.WriteBoolean("truncated", run.StandardOutputTruncated);
        writer.NullableEnum("encoding", run.StandardOutputEncoding);
        writer.WriteEndObject();
        writer.WriteStartObject("stderr");
        writer.WriteNumber("bytes", run.StandardErrorBytes);
        writer.WriteBoolean("truncated", run.StandardErrorTruncated);
        writer.WriteBoolean("shown", showStderr);
        writer.SafeString("text", showStderr ? run.StandardError : null);
        writer.WriteEndObject();
        writer.WriteStartObject("cleanup");
        writer.WriteString("status", run.Cleanup.Status.ToString());
        writer.WriteBoolean("rootExitConfirmed", run.Cleanup.RootExitConfirmed);
        writer.WriteBoolean("jobObjectAssigned", run.Cleanup.JobObjectAssigned);
        writer.NullableNumber("remainingJobProcesses", run.Cleanup.RemainingJobProcesses);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteExecutionHuman(HumanWriter writer, PluginExecutionResult run, bool showStderr)
    {
        writer.Value("Plugin exit code", run.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "(none: not started or did not exit)");
        writer.Value("Duration", string.Create(CultureInfo.InvariantCulture, $"{(long)run.Elapsed.TotalMilliseconds} ms"));
        if (run.TerminationReason != ExecutionTerminationReason.None)
        {
            writer.Value("Terminated by TrayWisp", run.TerminationReason.ToString());
        }

        if (run.Interpreter is PluginInterpreter interpreter)
        {
            writer.Field("Interpreter", interpreter.Path);
            using (writer.Indent())
            {
                writer.Value("Kind and source", $"{interpreter.Kind}, {interpreter.Source} (validated path; not authenticated)");
            }
        }

        if (run.StartedAtUtc is null)
        {
            writer.Line("No process was started.");
            return;
        }

        writer.Value("Cleanup", run.Cleanup.Status + (run.Cleanup.JobObjectAssigned ? " (job object assigned)" : " (no job object)"));
        writer.Value("Standard output", CommandSupport.Bytes(run.StandardOutputBytes)
            + (run.StandardOutputComplete ? ", complete" : ", INCOMPLETE")
            + (run.StandardOutputTruncated ? ", over the limit" : string.Empty));
        if (!showStderr)
        {
            writer.Value("Standard error", CommandSupport.Bytes(run.StandardErrorBytes) + (run.StandardErrorBytes > 0 ? " (not shown; use --show-stderr)" : string.Empty));
            return;
        }

        writer.Value("Standard error", CommandSupport.Bytes(run.StandardErrorBytes) + (run.StandardErrorTruncated ? ", shortened tail" : string.Empty) + ":");
        using (writer.Indent())
        {
            foreach (string line in TerminalText.Lines(run.StandardError, MaxStandardErrorLines))
            {
                writer.Line("| " + line);
            }
        }
    }

    // One trust check of the run's approval after the run: its decision, or that it was cancelled (also when cancellation
    // was requested before it started). Any other failure propagates, as for every other trust check.
    private sealed record TrustCheck(PluginTrustDecision? Decision, bool Cancelled)
    {
        public bool Approved => Decision is { IsApproved: true };

        public static TrustCheck Make(PluginTrustService trust, PluginApprovalIdentity approval, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new TrustCheck(null, Cancelled: true);
            }

            try
            {
                return new TrustCheck(trust.CheckTrust(approval, cancellationToken), Cancelled: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return new TrustCheck(null, Cancelled: true);
            }
        }
    }
}
