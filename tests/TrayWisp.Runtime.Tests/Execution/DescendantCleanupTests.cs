using System.Diagnostics;
using System.Runtime.Versioning;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// A run owns the processes tracked in its job object: those still running after the interpreter exits by itself are
// terminated, their exit must be confirmed, and output is suppressed when it cannot be. Every helper process records
// its PID; ExecutionTestEnvironment.Dispose kills any that survive, even when an assertion failed first.
[SupportedOSPlatform("windows")]
public sealed class DescendantCleanupTests : IDisposable
{
    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(300);

    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    public void Dispose() => _env.Dispose();

    [Fact]
    public async Task ADescendantLeftBySuccessfulRunIsTerminatedAndTheOutputStillPublished()
    {
        string pid = _env.PidFile("detached");
        _env.Approved("a.ps1", $"spawn-detached {pid}|60000", "out Title");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(30));

        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.Equal("Title", Assert.Single(result.Output!.Titles).Item.Text);
        Assert.Equal(0, result.ExitCode);

        // Natural interpreter exit: TrayWisp did not stop the interpreter, only what it left behind.
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
        Assert.False(result.TimedOut);
        Assert.Equal(ProcessCleanupStatus.DescendantsTerminated, result.Cleanup.Status);
        Assert.True(result.Cleanup.RootExitConfirmed);
        Assert.True(result.Cleanup.JobObjectAssigned);
        Assert.Equal(0, result.Cleanup.RemainingJobProcesses);
        ExecutionDiagnostic terminated = Assert.Single(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.DescendantsTerminated);
        Assert.Equal(PluginDiagnosticSeverity.Warning, terminated.Severity);
        Assert.Contains("confirmed", terminated.Message, StringComparison.Ordinal);
        Assert.Contains("exited normally", terminated.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code is ExecutionDiagnosticCode.CleanupIncomplete or ExecutionDiagnosticCode.TimedOut);
        ExecutionTestEnvironment.AssertExited(pid, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ADescendantLeftByANonZeroExitIsTerminatedToo()
    {
        string pid = _env.PidFile("detached");
        _env.Approved("a.ps1", $"spawn-detached {pid}|60000", "out Title", "exit 5");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(30));

        Assert.Equal(PluginExecutionStatus.NonZeroExitCode, result.Status);
        Assert.Equal(5, result.ExitCode);
        Assert.Null(result.Output);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
        Assert.Equal(ProcessCleanupStatus.DescendantsTerminated, result.Cleanup.Status);
        Assert.Equal(0, result.Cleanup.RemainingJobProcesses);
        ExecutionTestEnvironment.AssertExited(pid, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ADescendantThatClosedTheRedirectedPipesIsTerminated()
    {
        string pid = _env.PidFile("releaser");
        _env.Approved("a.ps1", $"spawn-releaser {pid}|60000", "out Title");
        var watch = Stopwatch.StartNew();

        PluginExecutionResult result = await _env.RunAsync(_env.Service(), "a.ps1", TimeSpan.FromSeconds(30));

        // It inherited the pipes and closed them, so the run completed without waiting for the timeout.
        Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
        Assert.True(result.StandardOutputComplete);
        Assert.Equal(ProcessCleanupStatus.DescendantsTerminated, result.Cleanup.Status);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20));
        ExecutionTestEnvironment.AssertExited(pid, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RepeatedRunsDoNotAccumulateTrackedDescendants()
    {
        PluginExecutionService service = _env.Service();
        for (int i = 0; i < 5; i++)
        {
            string pid = _env.PidFile($"detached{i}");
            _env.Approved($"p{i}.ps1", $"spawn-detached {pid}|60000", "out Title");

            PluginExecutionResult result = await _env.RunAsync(service, $"p{i}.ps1", TimeSpan.FromSeconds(30));

            Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
            Assert.Equal(ProcessCleanupStatus.DescendantsTerminated, result.Cleanup.Status);

            // Confirmed before the next run starts: nothing from earlier runs is still alive.
            ExecutionTestEnvironment.AssertExited(pid, TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task AJobThatCannotBeTerminatedIsIncompleteCleanupAndSuppressesOutput()
    {
        string pid = _env.PidFile("detached");
        _env.Approved("a.ps1", $"spawn-detached {pid}|60000", "out Title");
        var hooks = new ExecutionTestHooks { FailJobTermination = true, CleanupGracePeriod = ShortGrace };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(hooks: hooks), "a.ps1", TimeSpan.FromSeconds(30));

        Assert.Equal(PluginExecutionStatus.CleanupIncomplete, result.Status);
        Assert.Null(result.Output);
        Assert.True(result.StandardOutputComplete);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
        Assert.Equal(ProcessCleanupStatus.Incomplete, result.Cleanup.Status);
        Assert.True(result.Cleanup.RootExitConfirmed);
        Assert.True(result.Cleanup.RemainingJobProcesses >= 1);
        ExecutionDiagnostic incomplete = Assert.Single(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.CleanupIncomplete);
        Assert.Contains("terminating the job failed", incomplete.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.DescendantsTerminated);

        // Bounded: the settle time and the grace period, not the descendant's 60 s.
        Assert.True(result.Elapsed < PluginProcessRun.DescendantSettleTime + ShortGrace + TimeSpan.FromSeconds(10), $"Elapsed {result.Elapsed}.");

        // Kill-on-close stayed set: closing the job at the end of the run terminated the descendant.
        ExecutionTestEnvironment.AssertExited(pid);
    }

    [Fact]
    public async Task AJobThatCannotBeQueriedIsNeverTakenAsEmpty()
    {
        _env.Approved("a.ps1", "out Title");
        var hooks = new ExecutionTestHooks { FailJobQuery = true, CleanupGracePeriod = ShortGrace };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(hooks: hooks), "a.ps1", TimeSpan.FromSeconds(30));

        Assert.Equal(PluginExecutionStatus.CleanupIncomplete, result.Status);
        Assert.Null(result.Output);
        Assert.Equal(ExecutionTerminationReason.None, result.TerminationReason);
        Assert.Equal(ProcessCleanupStatus.Incomplete, result.Cleanup.Status);
        Assert.True(result.Cleanup.JobObjectAssigned);
        Assert.Null(result.Cleanup.RemainingJobProcesses);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.CleanupIncomplete && d.Message.Contains("could not be queried", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AJobThatCannotBeQueriedAfterATerminationIsIncomplete()
    {
        _env.Approved("a.ps1", "sleep 60000");
        var hooks = new ExecutionTestHooks { FailJobQuery = true, CleanupGracePeriod = ShortGrace, MinTimeout = TimeSpan.FromMilliseconds(100) };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(hooks: hooks), "a.ps1", TimeSpan.FromMilliseconds(300));

        Assert.Equal(PluginExecutionStatus.CleanupIncomplete, result.Status);
        Assert.Equal(ExecutionTerminationReason.Timeout, result.TerminationReason);
        Assert.True(result.Cleanup.RootExitConfirmed);
        Assert.Null(result.Cleanup.RemainingJobProcesses);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.CleanupIncomplete && d.Message.Contains("could not be queried", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutAJobObjectDescendantsAreUntrackedAndNothingIsClaimedAboutThem()
    {
        string pid = _env.PidFile("detached");
        _env.Approved("a.ps1", $"spawn-detached {pid}|60000", "out Title");

        PluginExecutionResult result = await _env.RunAsync(_env.Service(hooks: new ExecutionTestHooks { DisableJobObject = true }), "a.ps1", TimeSpan.FromSeconds(30));

        using Process? detached = ExecutionTestEnvironment.ProcessFromPidFile(pid);
        try
        {
            // The best-effort fallback cannot see a descendant that released the pipes; it is neither confirmed
            // gone nor reported as cleaned up.
            Assert.Equal(PluginExecutionStatus.Succeeded, result.Status);
            Assert.Equal(ProcessCleanupStatus.Exited, result.Cleanup.Status);
            Assert.False(result.Cleanup.JobObjectAssigned);
            Assert.Null(result.Cleanup.RemainingJobProcesses);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code is ExecutionDiagnosticCode.DescendantsTerminated or ExecutionDiagnosticCode.CleanupIncomplete);
        }
        finally
        {
            detached?.Kill();
            detached?.WaitForExit(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task AFailedJobAssignmentFallsBackToBestEffortTreeTermination()
    {
        string pid = _env.PidFile("holder");
        _env.Approved("a.ps1", $"spawn-holder {pid}|60000", "sleep 60000");
        var hooks = new ExecutionTestHooks { FailJobAssignment = true, MinTimeout = TimeSpan.FromMilliseconds(100) };

        PluginExecutionResult result = await _env.RunAsync(_env.Service(hooks: hooks), "a.ps1", TimeSpan.FromSeconds(3));

        Assert.Equal(PluginExecutionStatus.TimedOut, result.Status);
        Assert.False(result.Cleanup.JobObjectAssigned);
        Assert.Null(result.Cleanup.RemainingJobProcesses);
        Assert.Equal(ProcessCleanupStatus.Terminated, result.Cleanup.Status);
        Assert.Contains(result.Diagnostics, d => d.Code == ExecutionDiagnosticCode.JobObjectUnavailable && d.Message.Contains("could not be added", StringComparison.Ordinal));
        ExecutionTestEnvironment.AssertExited(pid);
    }
}
