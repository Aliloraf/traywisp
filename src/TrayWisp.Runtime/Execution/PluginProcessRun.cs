using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Execution;

// What happened to one launched (or not launchable) process.
internal sealed class ProcessRunOutcome
{
    public bool Started { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public TimeSpan Elapsed { get; init; }

    public int? ExitCode { get; init; }

    public ExecutionTerminationReason TerminationReason { get; init; }

    public PluginProcessCleanup Cleanup { get; init; } = PluginProcessCleanup.NotStarted;

    public byte[] StandardOutput { get; init; } = [];

    public long StandardOutputBytes { get; init; }

    public bool StandardOutputComplete { get; init; }

    public bool StandardOutputOverflowed { get; init; }

    public byte[] StandardError { get; init; } = [];

    public long StandardErrorBytes { get; init; }

    public bool StandardErrorOverflowed { get; init; }
}

// The setup steps of supervision after Process.Start, in order. Test hooks can inject a failure before each one.
internal enum SupervisionStep
{
    JobAssignment,
    StandardInput,
    OutputStream,
    ErrorStream,
    OutputCapture,
    ErrorCapture,
    ExitMonitoring,
}

// Starts one interpreter process with prepared start information and supervises it until it has ended or TrayWisp
// has given up on it. Only PluginExecutionService calls this, after an approved trust check, with the lease of that
// check held for the whole call. See docs/execution-engine.md ("Process execution" and "Process cleanup").
//
// Once Process.Start has returned a process, SuperviseAsync owns it: every path out of it, including a failure of any
// setup step and an exception it does not handle, first attempts to terminate the process (unless cleanup already
// ran) and stops and releases whatever capture was set up. Disposing Process does not terminate the process.
[SupportedOSPlatform("windows")]
internal sealed class PluginProcessRun(TimeProvider time, ExecutionTestHooks? hooks, ExecutionDiagnosticBag diagnostics)
{
    // How long, after the cleanup grace period, stopped reads are given to return.
    internal static readonly TimeSpan CaptureStopWait = TimeSpan.FromSeconds(1);

    // How long, after a normal exit, processes still in the job are given to exit by themselves before TrayWisp
    // terminates them. The console host Windows creates for a windowless console process is in the job and exits
    // just after the process.
    internal static readonly TimeSpan DescendantSettleTime = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan JobPollInterval = TimeSpan.FromMilliseconds(20);

    private static readonly TimeSpan StopRetryInterval = TimeSpan.FromMilliseconds(20);

    // Access denied, reported by the injected job failures of the test hooks.
    private const int InjectedJobError = 5;

    // The lease is not used here; requiring it makes an unauthorized call impossible to write by accident.
    public async Task<ProcessRunOutcome> RunAsync(PluginExecutionLease lease, ProcessStartInfo startInfo, TimeSpan timeout, TimeSpan grace, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(lease.IsDisposed, lease);
        WindowsJobObject? job = null;
        if (hooks?.DisableJobObject != true)
        {
            job = WindowsJobObject.TryCreate(out int jobError);
            if (job is null)
            {
                diagnostics.Add(
                    PluginDiagnosticSeverity.Warning,
                    ExecutionDiagnosticCode.JobObjectUnavailable,
                    $"A job object could not be created ({TrustPaths.Quote(WindowsFileApi.Describe(jobError))}). Only best-effort process-tree termination is available for this run.");
            }
        }

        using (job)
        {
            hooks?.BeforeProcessStart?.Invoke(startInfo);

            // The last cancellation check before the process exists. A cancellation that arrives after it is handled
            // as a cancellation of the running process.
            cancellationToken.ThrowIfCancellationRequested();
            long startTimestamp = time.GetTimestamp();
            Process? process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception exception)
            {
                // CreateProcess failed: no process exists. Other exceptions from Process.Start mean an invalid start
                // information, which TrayWisp builds itself, and are programming errors that propagate.
                diagnostics.Add(
                    PluginDiagnosticSeverity.Error,
                    ExecutionDiagnosticCode.ProcessStartFailed,
                    $"The interpreter {TrustPaths.Quote(startInfo.FileName)} could not be started: {TrustPaths.Quote(WindowsFileApi.Describe(exception.NativeErrorCode))} (error {exception.NativeErrorCode}).");
                return new ProcessRunOutcome();
            }

            if (process is null)
            {
                diagnostics.Add(PluginDiagnosticSeverity.Error, ExecutionDiagnosticCode.ProcessStartFailed, $"The interpreter {TrustPaths.Quote(startInfo.FileName)} could not be started.");
                return new ProcessRunOutcome();
            }

            // The job is disposed after the process: with kill-on-close, closing it terminates whatever it still holds.
            using (process)
            {
                return await SuperviseAsync(process, job, startTimestamp, timeout, grace, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<ProcessRunOutcome> SuperviseAsync(Process process, WindowsJobObject? job, long startTimestamp, TimeSpan timeout, TimeSpan grace, CancellationToken cancellationToken)
    {
        // The cleanup boundary starts here, before anything that can fail.
        var run = new SupervisedProcess(process, job, time, startTimestamp, timeout);
        bool cleanedUp = false;
        CancellationTokenRegistration registration = default;
        try
        {
            DateTimeOffset startedAt = time.GetUtcNow();

            // Registered first, so a cancellation at any point after the start (also during setup) is recorded.
            registration = cancellationToken.Register(static state => ((RunArbiter)state!).Record(RunEvent.Cancellation), run.Arbiter);
            SetUp(run);

            RunEvent decision = await DecideAsync(run.Arbiter, run.RunEnd).ConfigureAwait(false);
            ExecutionTerminationReason reason = RunArbiter.ToTerminationReason(decision);
            if (run.ExitMonitoringFault is { } fault)
            {
                diagnostics.Add(
                    PluginDiagnosticSeverity.Error,
                    ExecutionDiagnosticCode.SupervisionFailed,
                    $"Monitoring the exit of the plugin process failed ({Describe(fault)}). The failure is not taken as proof that the process exited; it was terminated and its output rejected.");
            }

            if (reason == ExecutionTerminationReason.Timeout)
            {
                ReportTimeoutState(run);
            }

            PluginProcessCleanup cleanup = reason == ExecutionTerminationReason.None
                ? await FinishNormallyAsync(run, grace).ConfigureAwait(false)
                : await TerminateAsync(run, grace).ConfigureAwait(false);
            cleanedUp = true;

            byte[] output = run.Stdout.Freeze();
            byte[] error = run.Stderr.Freeze();
            return new ProcessRunOutcome
            {
                Started = true,
                StartedAtUtc = startedAt,
                Elapsed = time.GetElapsedTime(startTimestamp),
                ExitCode = cleanup.RootExitConfirmed ? TryGetExitCode(process) : null,
                TerminationReason = reason,
                Cleanup = cleanup,
                StandardOutput = output,
                StandardOutputBytes = run.Stdout.TotalBytes,
                StandardOutputComplete = reason == ExecutionTerminationReason.None && run.Stdout.ReachedEnd && !run.Stdout.Overflowed,
                StandardOutputOverflowed = run.Stdout.Overflowed,
                StandardError = error,
                StandardErrorBytes = run.Stderr.TotalBytes,
                StandardErrorOverflowed = run.Stderr.Overflowed,
            };
        }
        finally
        {
            registration.Dispose();
            if (!cleanedUp)
            {
                // Only an exception that is not an operational failure (a fatal runtime failure or a programming
                // error) reaches this: never leave the process behind. The job is then closed with kill-on-close.
                Terminate(run);
            }

            // Stop reads that are still pending, freeze both captures, and dispose each stream once its read loop has
            // ended (at once if its loop never started). A read that could not be interrupted ends when the last writer
            // of the pipe exits; it keeps nothing after the freeze.
            run.Stdout.RequestStop();
            run.Stderr.RequestStop();
            run.Stdout.Freeze();
            run.Stderr.Freeze();
            hooks?.CaptureLoopsEnded?.Invoke(run.CaptureLoops);
            DisposeWhenDone(run.Stdout, run.StdoutStream);
            DisposeWhenDone(run.Stderr, run.StderrStream);
            await run.EndAsync().ConfigureAwait(false);
            run.Dispose();
        }
    }

    // The setup steps after the start. An operational failure of any step (one the operating system or .NET reports
    // for a stream, a thread or exit monitoring) is recorded as a supervision failure, which ends the run with
    // termination. Other exceptions propagate after the cleanup in SuperviseAsync.
    private void SetUp(SupervisedProcess run)
    {
        RunArbiter arbiter = run.Arbiter;
        try
        {
            hooks?.ProcessStarted?.Invoke(run.Process);
            Step(SupervisionStep.JobAssignment);
            if (run.Job is not null && !TryAssign(run.Job, run.Process, out int assignError))
            {
                diagnostics.Add(
                    PluginDiagnosticSeverity.Warning,
                    ExecutionDiagnosticCode.JobObjectUnavailable,
                    $"The plugin process could not be added to a job object ({TrustPaths.Quote(WindowsFileApi.Describe(assignError))}). Only best-effort process-tree termination is available for this run.");
            }

            // TrayWisp never writes to the plugin's standard input: it is closed at once, so a read sees its end.
            Step(SupervisionStep.StandardInput);
            try
            {
                run.Process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process already exited and closed its end of the pipe.
            }

            Step(SupervisionStep.OutputStream);
            run.StdoutStream = run.Process.StandardOutput.BaseStream;
            Step(SupervisionStep.ErrorStream);
            run.StderrStream = run.Process.StandardError.BaseStream;
            Step(SupervisionStep.OutputCapture);
            run.Stdout.Start(
                run.StdoutStream,
                hooks?.StandardOutputRead,
                () => arbiter.Record(RunEvent.OutputLimit),
                () => arbiter.Record(RunEvent.CaptureFailure),
                arbiter.RecordOutputEnd);
            Step(SupervisionStep.ErrorCapture);
            run.Stderr.Start(run.StderrStream, onRead: null, onOverflow: static () => { }, () => arbiter.Record(RunEvent.CaptureFailure), arbiter.RecordErrorEnd);
            Step(SupervisionStep.ExitMonitoring);
            run.MonitorExit((hooks?.WaitForExit ?? DefaultWaitForExit)(run.Process, run.RunEnd));
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            arbiter.Record(RunEvent.SupervisionFailure);
            diagnostics.Add(
                PluginDiagnosticSeverity.Error,
                ExecutionDiagnosticCode.SupervisionFailed,
                $"Supervision of the started plugin process could not be set up ({Describe(exception)}). It was terminated and its output rejected.");
        }

        void Step(SupervisionStep step) => hooks?.BeforeSupervisionStep?.Invoke(step);
    }

    // Waits until the arbiter can decide, then decides. The supervisor may resume late (the thread pool can be busy):
    // the decision uses the recorded times of the events, not the time it resumed.
    private async Task<RunEvent> DecideAsync(RunArbiter arbiter, CancellationToken runEnd)
    {
        while (true)
        {
            TimeSpan remaining = arbiter.TimeUntilDeadline();
            if (remaining > TimeSpan.Zero && !arbiter.Decisive.IsCompleted)
            {
                // Always resume on the thread pool: the task that completes the wait can be a timer's, and continuing
                // on the timer thread would delay every other timer of the process while this run's code (and the
                // caller's continuation) runs.
                await Task.WhenAny(arbiter.Decisive, Task.Delay(remaining, time, runEnd)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }

            if (hooks?.BeforeDecision is { } beforeDecision)
            {
                await beforeDecision(arbiter).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }

            // Null only when a timer fired before the deadline was reached: wait for the rest of it.
            if (arbiter.TryDecide() is { } decision)
            {
                return decision;
            }
        }
    }

    // Says what was still missing when the timeout elapsed: a root that has exited while its output is still open
    // usually means a process it started holds the output pipes.
    private void ReportTimeoutState(SupervisedProcess run)
    {
        bool outputEnded = run.Stdout.ReachedEnd;
        bool errorEnded = run.Stderr.ReachedEnd;
        diagnostics.Add(
            PluginDiagnosticSeverity.Info,
            ExecutionDiagnosticCode.TimedOut,
            run.RootExited()
                ? $"When the timeout elapsed, the plugin process had exited, but {(outputEnded ? "standard error" : errorEnded ? "standard output" : "standard output and standard error")} "
                  + "had not been closed by every process holding it, usually a process the plugin started."
                : "When the timeout elapsed, the plugin process was still running.");
    }

    // The interpreter exited by itself and both output streams ended. The run owns the processes in its job: any that
    // remain after a short settle time are terminated, and their exit must be confirmed. Without a job object,
    // descendants that released the output streams cannot be seen, and nothing is claimed about them.
    private async Task<PluginProcessCleanup> FinishNormallyAsync(SupervisedProcess run, TimeSpan grace)
    {
        WindowsJobObject? job = run.Job;
        if (job?.IsAssigned != true)
        {
            return new PluginProcessCleanup(ProcessCleanupStatus.Exited, rootExitConfirmed: true, jobObjectAssigned: false, remainingJobProcesses: null);
        }

        // An unknown count (a failed query) is never taken as an empty job.
        int? remaining = QueryJob(job);
        long settleStart = time.GetTimestamp();
        while (remaining is not 0 && time.GetElapsedTime(settleStart) < DescendantSettleTime)
        {
            await Task.Delay(JobPollInterval, time, run.RunEnd).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            remaining = QueryJob(job);
        }

        if (remaining == 0)
        {
            return new PluginProcessCleanup(ProcessCleanupStatus.Exited, rootExitConfirmed: true, jobObjectAssigned: true, remainingJobProcesses: 0);
        }

        int? found = remaining;
        int terminateError = 0;
        bool terminated = hooks?.SuppressTermination != true && TryTerminateJob(job, out terminateError);
        long cleanupStart = time.GetTimestamp();
        remaining = QueryJob(job);
        while (remaining is not 0 && time.GetElapsedTime(cleanupStart) < grace)
        {
            await Task.Delay(JobPollInterval, time, run.RunEnd).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            remaining = QueryJob(job);
        }

        string what = found is null
            ? "The job object of the run could not be queried after the plugin exited, so TrayWisp could not tell whether processes it started were still running"
            : string.Create(CultureInfo.InvariantCulture, $"{found} process(es) started by the plugin were still running in its job object after it exited by itself");
        if (remaining == 0)
        {
            diagnostics.Add(
                PluginDiagnosticSeverity.Warning,
                ExecutionDiagnosticCode.DescendantsTerminated,
                $"{what}. A plugin run owns the processes it starts, so TrayWisp terminated them and confirmed that they exited. "
                + "Background processes that outlive a run are not supported. The plugin process itself exited normally.");
            return new PluginProcessCleanup(ProcessCleanupStatus.DescendantsTerminated, rootExitConfirmed: true, jobObjectAssigned: true, remainingJobProcesses: 0);
        }

        string action = terminated
            ? "TrayWisp terminated the job"
            : hooks?.SuppressTermination == true
                ? "termination was suppressed"
                : $"terminating the job failed ({TrustPaths.Quote(WindowsFileApi.Describe(terminateError))})";
        diagnostics.Add(
            PluginDiagnosticSeverity.Error,
            ExecutionDiagnosticCode.CleanupIncomplete,
            $"{what}. {action}, but could not confirm within {grace.TotalSeconds:0.###} s that every process in it exited "
            + $"({(remaining is null ? "its process count could not be queried" : string.Create(CultureInfo.InvariantCulture, $"{remaining} still reported"))}). "
            + "Processes started for the plugin may still be running, so its output is not published.");
        return new PluginProcessCleanup(ProcessCleanupStatus.Incomplete, rootExitConfirmed: true, jobObjectAssigned: true, remaining);
    }

    private async Task<PluginProcessCleanup> TerminateAsync(SupervisedProcess run, TimeSpan grace)
    {
        // A separate, bounded cleanup period: the caller's token may already be cancelled.
        long cleanupStart = time.GetTimestamp();
        if (hooks?.SuppressTermination != true)
        {
            Terminate(run);
        }

        TimeSpan Remaining() => grace - time.GetElapsedTime(cleanupStart);
        bool rootExited = await RootExitsWithinAsync(run, Remaining()).ConfigureAwait(false);
        bool streamsEnded = await CompletesWithinAsync(run.CaptureLoops, Remaining(), run.RunEnd).ConfigureAwait(false);
        if (!streamsEnded)
        {
            // Interrupt the reads, repeating in case a read starts just after a request, for a bounded time.
            long stopStart = time.GetTimestamp();
            do
            {
                run.Stdout.RequestStop();
                run.Stderr.RequestStop();
            }
            while (!await CompletesWithinAsync(run.CaptureLoops, StopRetryInterval, run.RunEnd).ConfigureAwait(false) && time.GetElapsedTime(stopStart) < CaptureStopWait);
        }

        WindowsJobObject? job = run.Job;
        int? remaining = null;
        bool assigned = job?.IsAssigned == true;
        if (assigned)
        {
            remaining = QueryJob(job!);
            while (remaining is not 0 && Remaining() > TimeSpan.Zero)
            {
                await Task.Delay(JobPollInterval, time, run.RunEnd).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                remaining = QueryJob(job!);
            }
        }

        bool streamsClosed = run.Stdout.ReachedEnd && run.Stderr.ReachedEnd;
        bool confirmed = rootExited && streamsClosed && (!assigned || remaining == 0);
        if (!confirmed)
        {
            string what = !rootExited ? "the plugin process exited"
                : !streamsClosed && !(run.Stdout.Started && run.Stderr.Started) ? "its output streams were closed (one was never read, because supervision could not be set up)"
                : !streamsClosed ? "its output streams were closed by every process holding them"
                : remaining is null ? "every process in its job object exited (the job object could not be queried)"
                : "every process in its job object exited";
            diagnostics.Add(
                PluginDiagnosticSeverity.Error,
                ExecutionDiagnosticCode.CleanupIncomplete,
                $"After terminating the run, TrayWisp could not confirm within {grace.TotalSeconds:0.###} s that {what}. Processes started for the plugin may still be running.");
        }

        return new PluginProcessCleanup(confirmed ? ProcessCleanupStatus.Terminated : ProcessCleanupStatus.Incomplete, rootExited, assigned, remaining);
    }

    // Best-effort termination: every process in the job, then the process tree that Windows still links to the root.
    private void Terminate(SupervisedProcess run)
    {
        if (run.Job?.IsAssigned == true)
        {
            TryTerminateJob(run.Job, out _);
        }

        try
        {
            run.Process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException or AggregateException)
        {
            // Already exited, or some processes could not be terminated; the cleanup checks report what remains.
        }
    }

    // Root exit, confirmed by the exit monitor or, when monitoring is unavailable or failed, by polling the process.
    private async Task<bool> RootExitsWithinAsync(SupervisedProcess run, TimeSpan limit)
    {
        long start = time.GetTimestamp();
        while (!run.RootExited())
        {
            TimeSpan left = limit - time.GetElapsedTime(start);
            if (left <= TimeSpan.Zero)
            {
                return false;
            }

            TimeSpan wait = run.ExitMonitoringHealthy ? left : TimeSpan.FromTicks(Math.Min(left.Ticks, JobPollInterval.Ticks));
            await Task.WhenAny(run.ExitObserved, Task.Delay(wait, time, run.RunEnd)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        return true;
    }

    private bool TryAssign(WindowsJobObject job, Process process, out int error)
    {
        if (hooks?.FailJobAssignment == true)
        {
            error = InjectedJobError;
            return false;
        }

        return job.TryAssign(process.SafeHandle, out error);
    }

    private int? QueryJob(WindowsJobObject job) => hooks?.FailJobQuery == true ? null : job.ActiveProcesses();

    private bool TryTerminateJob(WindowsJobObject job, out int error)
    {
        if (hooks?.FailJobTermination == true)
        {
            error = InjectedJobError;
            return false;
        }

        return job.TryTerminate(exitCode: 1, out error);
    }

    private async Task<bool> CompletesWithinAsync(Task task, TimeSpan limit, CancellationToken runEnd)
    {
        if (task.IsCompleted)
        {
            return true;
        }

        if (limit > TimeSpan.Zero)
        {
            await Task.WhenAny(task, Task.Delay(limit, time, runEnd)).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }

        return task.IsCompleted;
    }

    private static Task DefaultWaitForExit(Process process, CancellationToken runEnd) => process.WaitForExitAsync(runEnd);

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Failures the operating system or .NET reports for pipes, threads and process monitoring. Anything else (out of
    // memory, a programming error) is not converted into a result.
    private static bool IsOperationalFailure(Exception exception) =>
        exception is IOException or Win32Exception or InvalidOperationException or ObjectDisposedException
            or NotSupportedException or UnauthorizedAccessException or ThreadStartException;

    // The type and HRESULT of an exception, never its message.
    private static string Describe(Exception exception) => string.Create(CultureInfo.InvariantCulture, $"{exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}");

    private static void DisposeWhenDone(StreamCapture capture, Stream? stream)
    {
        if (stream is null)
        {
            return;
        }

        if (!capture.Started || capture.Completion.IsCompleted)
        {
            DisposeQuietly(stream);
        }
        else
        {
            _ = capture.Completion.ContinueWith(static (_, state) => DisposeQuietly((Stream)state!), stream, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    // Disposing a pipe stream can fail when the pipe is already broken; nothing is lost then.
    private static void DisposeQuietly(Stream stream)
    {
        try
        {
            stream.Dispose();
        }
        catch (IOException)
        {
        }
    }
}

// The state of one supervised process: its captures, streams, exit monitoring and arbiter.
[SupportedOSPlatform("windows")]
internal sealed class SupervisedProcess : IDisposable
{
    private readonly CancellationTokenSource _runEnd = new();
    private readonly TaskCompletionSource _exitObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _exitMonitored;
    private volatile bool _exitConfirmed;
    private volatile Exception? _exitFault;

    public SupervisedProcess(Process process, WindowsJobObject? job, TimeProvider time, long startTimestamp, TimeSpan timeout)
    {
        Process = process;
        Job = job;
        Arbiter = new RunArbiter(time, startTimestamp, timeout);
        RunEnd = _runEnd.Token;
    }

    public Process Process { get; }

    public WindowsJobObject? Job { get; }

    public RunArbiter Arbiter { get; }

    public StreamCapture Stdout { get; } = new(PluginExecutionService.MaxStandardOutputBytes, keepTail: false);

    public StreamCapture Stderr { get; } = new(PluginExecutionService.MaxStandardErrorBytes, keepTail: true);

    public Stream? StdoutStream { get; set; }

    public Stream? StderrStream { get; set; }

    // Cancelled when supervision ends, which ends exit monitoring and pending delays.
    public CancellationToken RunEnd { get; }

    // Completes when the exit monitor reported an exit or failed.
    public Task ExitObserved => _exitObserved.Task;

    // Exit monitoring was set up and has not failed.
    public bool ExitMonitoringHealthy => _exitMonitored && _exitFault is null;

    public Exception? ExitMonitoringFault => _exitFault;

    // Completes when every capture loop that was started has ended.
    public Task CaptureLoops => Task.WhenAll(new[] { Stdout, Stderr }.Where(capture => capture.Started).Select(capture => capture.Completion));

    public void MonitorExit(Task exitTask)
    {
        _exitMonitored = true;
        _ = exitTask.ContinueWith(
            static (task, state) => ((SupervisedProcess)state!).OnExitTaskCompleted(task),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // Whether the interpreter process has exited: reported by the exit monitor, or, without a healthy monitor, read
    // from the process itself. A failed monitor is never taken as an exit.
    public bool RootExited()
    {
        if (_exitConfirmed)
        {
            return true;
        }

        if (ExitMonitoringHealthy)
        {
            return false;
        }

        try
        {
            return Process.HasExited;
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public Task EndAsync() => _runEnd.CancelAsync();

    public void Dispose() => _runEnd.Dispose();

    private void OnExitTaskCompleted(Task task)
    {
        if (task.IsCompletedSuccessfully)
        {
            _exitConfirmed = true;
            Arbiter.RecordExit();
        }
        else if (task.IsFaulted)
        {
            // Observes the exception, so it is never reported as unobserved.
            _exitFault = task.Exception!.InnerException ?? task.Exception;
            Arbiter.Record(RunEvent.SupervisionFailure);
        }

        // A cancelled task means supervision ended first; it says nothing about the process.
        _exitObserved.TrySetResult();
    }
}
