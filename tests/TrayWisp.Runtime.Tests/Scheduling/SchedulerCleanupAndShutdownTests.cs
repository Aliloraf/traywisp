using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// Incomplete cleanup blocking, the safety capacity, faults and shutdown.
public sealed class SchedulerCleanupAndShutdownTests
{
    private static PluginId Id(string value) => PluginId.Create(value);

    [Theory]
    [InlineData("CleanupIncomplete")]
    [InlineData("TimedOutIncomplete")]
    public async Task IncompleteCleanupBlocksThePluginForTheSchedulersLifetime(string outcome)
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.1s.ps1");
        await harness.CompleteAsync("a.1s.ps1", Enum.Parse<RunOutcome>(outcome));

        ScheduledPlugin plugin = harness.Plugin("a.1s.ps1");
        Assert.Equal(PluginScheduleStatus.CleanupBlocked, plugin.Status);
        PluginCleanupBlock block = Assert.Single(harness.Snapshot.CleanupBlocks);
        Assert.Equal(Id("a.1s.ps1"), block.Id);
        Assert.Equal(plugin.LastRun!.RunId, block.RunId);
        Assert.Equal(harness.Snapshot.MaxConcurrency - 1, harness.Snapshot.AvailableSlots);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.CleanupIncomplete);

        // Nothing unblocks it: time, manual refresh, reapproval, trust notifications, disabling and enabling, removal and
        // rediscovery under the same (case-insensitive) identity.
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(RefreshRequestStatus.CleanupBlocked, harness.Refresh("a.1s.ps1"));
        harness.Ops.Revoke("a.1s.ps1");
        harness.Ops.Approve("a.1s.ps1");
        harness.Scheduler.NotifyTrustChanged(Id("a.1s.ps1"));
        harness.Scheduler.SetEnabled(Id("a.1s.ps1"), enabled: false);
        harness.Scheduler.SetEnabled(Id("a.1s.ps1"), enabled: true);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        harness.Ops.Delete("a.1s.ps1");
        await harness.RescanAsync();
        Assert.Empty(harness.Snapshot.Plugins);
        Assert.Single(harness.Snapshot.CleanupBlocks);
        harness.Ops.Add("A.1S.PS1");
        harness.Ops.Approve("A.1S.PS1");
        await harness.RescanAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(PluginScheduleStatus.CleanupBlocked, harness.Plugin("a.1s.ps1").Status);
        Assert.Equal(RefreshRequestStatus.CleanupBlocked, harness.Refresh("A.1S.PS1"));
        Assert.Single(harness.Ops.Runs);
    }

    [Fact]
    public async Task AnObsoleteRunStillRecordsIncompleteCleanup()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync("a.5s.ps1");
        FakeRun run = harness.Ops.LastRun("a.5s.ps1");

        // Removed while running; the cancelled run then ends without confirmed cleanup.
        harness.Ops.Delete("a.5s.ps1");
        await harness.RescanAsync();
        run.Complete(RunOutcome.CleanupIncomplete);
        await harness.SettleAsync();
        Assert.Single(harness.Snapshot.CleanupBlocks);

        harness.Ops.Add("a.5s.ps1");
        harness.Ops.Approve("a.5s.ps1");
        await harness.RescanAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Single(harness.Ops.Runs);
        Assert.Equal(PluginEligibility.CleanupBlocked, harness.Plugin("a.5s.ps1").Eligibility);
    }

    [Fact]
    public async Task UnresolvedCleanupIncidentsConsumeTheGlobalCapacity()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 2 });
        await harness.StartAsync("a.1s.ps1", "b.1s.ps1", "c.1s.ps1");

        // The incident keeps a's slot: c, waiting for a slot, does not get it.
        await harness.CompleteAsync("a.1s.ps1", RunOutcome.CleanupIncomplete);
        Assert.Equal(0, harness.Snapshot.AvailableSlots);
        Assert.Equal(PluginActivity.Queued, harness.Plugin("c.1s.ps1").Activity);
        Assert.Equal(2, harness.Ops.Runs.Count);

        // b's normal completion frees its slot for c; a second incident leaves no slot for anything.
        await harness.CompleteAsync("b.1s.ps1");
        Assert.Equal(3, harness.Ops.Runs.Count);
        await harness.CompleteAsync("c.1s.ps1", RunOutcome.CleanupIncomplete);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(3, harness.Ops.Runs.Count);
        Assert.Equal(0, harness.Snapshot.AvailableSlots);
        Assert.Equal(2, harness.Snapshot.CleanupBlocks.Length);
        Assert.Equal(PluginActivity.Queued, harness.Plugin("b.1s.ps1").Activity);
    }

    [Fact]
    public async Task AFullCleanupRegistryHaltsAllExecution()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 3 }, new SchedulerTestHooks { MaxCleanupIncidents = 1 });
        await harness.StartAsync("a.1s.ps1", "b.1s.ps1", "c.1s.ps1");
        await harness.CompleteAsync("a.1s.ps1", RunOutcome.CleanupIncomplete);
        Assert.False(harness.Snapshot.ExecutionHalted);

        // The second incident cannot be recorded: rather than forget it, the scheduler launches nothing more.
        await harness.CompleteAsync("b.1s.ps1", RunOutcome.CleanupIncomplete);
        Assert.True(harness.Snapshot.ExecutionHalted);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.CleanupRegistryFull);
        await harness.CompleteAsync("c.1s.ps1");
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(3, harness.Ops.Runs.Count);
        Assert.Equal(RefreshRequestStatus.ExecutionHalted, harness.Refresh("c.1s.ps1"));
        Assert.All(harness.Snapshot.Plugins, plugin => Assert.NotEqual(PluginActivity.Queued, plugin.Activity));
    }

    [Fact]
    public async Task AnEngineFailureFaultsTheSchedulerAndBlocksThePlugin()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.RunThrows = id => id.Value.StartsWith('b') ? new InvalidOperationException("bug") : null;
        await harness.StartAsync("a.5s.ps1", "b.5s.ps1");

        PluginSchedulerSnapshot snapshot = await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Faulted);
        Assert.True(snapshot.ExecutionHalted);
        SchedulerDiagnostic fault = Assert.Single(snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted);
        Assert.Contains("System.InvalidOperationException", fault.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("bug", fault.Message, StringComparison.Ordinal);

        // Unknown cleanup: blocked. Everything else is cancelled and nothing is published.
        Assert.Contains(snapshot.CleanupBlocks, block => block.Id == Id("b.5s.ps1"));
        // "a" may have been launched before the fault (then it was cancelled) or not at all (its launch then saw the
        // cancellation first).
        Assert.All(harness.Ops.Runs, run => Assert.True(run.CancellationRequested));
        Assert.Equal(RefreshRequestStatus.ExecutionHalted, harness.Refresh("a.5s.ps1"));
        Assert.Equal(PluginCommandStatus.NotRunning, harness.Scheduler.NotifyTrustChanged(Id("a.5s.ps1")));

        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(result.IsComplete);
        Assert.Equal(SchedulerLifecycle.Stopped, result.Lifecycle);
        Assert.Equal(1, result.UnresolvedCleanupIncidents);
    }

    [Fact]
    public async Task ShutdownCancelsActiveWorkLaunchesNothingQueuedAndWithdrawsOutput()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 1, ShutdownTimeout = TimeSpan.FromSeconds(5) });
        harness.Ops.Add("a.1s.ps1");
        harness.Ops.Approve("a.1s.ps1");
        harness.Ops.Add("b.1s.ps1");
        harness.Ops.Approve("b.1s.ps1");
        await harness.StartAsync();
        await harness.CompleteAsync("a.1s.ps1");
        Assert.NotNull(harness.Plugin("a.1s.ps1").Output);
        FakeRun active = Assert.Single(harness.Ops.ActiveRuns);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(PluginActivity.Queued, harness.Plugin("a.1s.ps1").Activity);

        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(result.IsComplete);
        Assert.True(active.CancellationRequested);
        Assert.Equal(SchedulerLifecycle.Stopped, harness.Snapshot.Lifecycle);
        Assert.All(harness.Snapshot.Plugins, plugin => Assert.Null(plugin.Output));
        Assert.All(harness.Snapshot.Plugins, plugin => Assert.Equal(PluginActivity.None, plugin.Activity));
        Assert.Equal(2, harness.Ops.Runs.Count);

        // Nothing more happens: no launches, no commands, no restart.
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Equal(2, harness.Ops.Runs.Count);
        Assert.Equal(RefreshRequestStatus.NotRunning, harness.Refresh("a.1s.ps1"));
        Assert.False(harness.Scheduler.RequestRescan());
        Assert.Equal(PluginCommandStatus.NotRunning, harness.Scheduler.SetEnabled(Id("a.1s.ps1"), enabled: false));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Scheduler.StartAsync(SchedulerHarness.Token));
        Assert.True(harness.Ops.Watchers[^1].Disposed);
    }

    [Fact]
    public async Task AnIncompleteShutdownIsReportedAndALateRunPublishesNothing()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.Zero });
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync("a.5s.ps1");
        FakeRun run = harness.Ops.LastRun("a.5s.ps1");

        // The run ignores cancellation (as a blocked operating-system call might).
        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(run.CancellationRequested);
        Assert.False(result.IsComplete);
        Assert.Equal(SchedulerLifecycle.Stopping, result.Lifecycle);
        Assert.Equal([Id("a.5s.ps1")], result.OutstandingRuns);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.ShutdownIncomplete);

        // The late success is never published and launches nothing; the lifecycle then reaches Stopped.
        run.Succeed();
        PluginSchedulerSnapshot stopped = await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Stopped);
        Assert.Null(stopped.Find(Id("a.5s.ps1"))!.Output);
        Assert.Equal(PluginRunDisposition.Superseded, stopped.Find(Id("a.5s.ps1"))!.LastRun!.Disposition);
        Assert.Equal(0, harness.Ops.ApprovalChecks);
        Assert.True((await harness.Scheduler.StopAsync(SchedulerHarness.Token)).IsComplete);
        Assert.Single(harness.Ops.Runs);
    }

    [Fact]
    public async Task ShutdownDuringATrustCheckReportsTheOutstandingReconciliation()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.Zero });
        harness.Ops.Add("a.5s.ps1");
        harness.Ops.Approve("a.5s.ps1");
        var gate = new Gate(observeCancellation: false);
        harness.Ops.CheckGate = gate;
        Task start = harness.Scheduler.StartAsync(SchedulerHarness.Token);
        await gate.Entered;

        // A trust check that does not observe cancellation (a synchronous file read in progress).
        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.False(result.IsComplete);
        Assert.True(result.ReconciliationOutstanding);
        Assert.Empty(result.OutstandingRuns);
        await start;

        // Its late approval is discarded: nothing is launched after shutdown began.
        gate.Release();
        await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Stopped);
        Assert.Empty(harness.Ops.Runs);
        Assert.Equal(PluginScheduleStatus.AwaitingTrustCheck, harness.Plugin("a.5s.ps1").Status);
    }

    [Fact]
    public async Task TheShutdownWaitIsBoundedInRealTime()
    {
        var root = TrayWisp.Core.Trust.PluginRoot.Create(@"C:\plugins");
        var ops = new FakeSchedulerOperations(root) { RunsHonorCancellation = false };
        ops.Add("a.ps1");
        ops.Approve("a.ps1");
        await using var scheduler = new PluginScheduler(ops, root, new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(200) }, TimeProvider.System, hooks: null);
        await scheduler.StartAsync(SchedulerHarness.Token);
        Assert.Equal(RefreshRequestStatus.Accepted, scheduler.RequestRefresh(Id("a.ps1")));
        await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30), SchedulerHarness.Token);
        FakeRun run = Assert.Single(ops.Runs);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        SchedulerStopResult result = await scheduler.StopAsync(SchedulerHarness.Token);
        Assert.False(result.IsComplete);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));

        // A cooperative end within the bound is reported as complete.
        run.Succeed();
        Assert.True((await scheduler.StopAsync(SchedulerHarness.Token)).IsComplete);
    }

    [Fact]
    public async Task StoppingBeforeStartingOrTwiceIsSafe()
    {
        await using var harness = new SchedulerHarness();
        SchedulerStopResult first = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(first.IsComplete);
        Assert.Equal(SchedulerLifecycle.Stopped, first.Lifecycle);
        Assert.True((await harness.Scheduler.StopAsync(SchedulerHarness.Token)).IsComplete);
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Scheduler.StartAsync(SchedulerHarness.Token));
        await harness.Scheduler.DisposeAsync();
        Assert.Empty(harness.Ops.Watchers);
        Assert.Equal(0, harness.Ops.Scans);
    }
}
