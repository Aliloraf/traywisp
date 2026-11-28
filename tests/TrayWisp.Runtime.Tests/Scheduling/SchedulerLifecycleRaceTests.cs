using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// Lifecycle races without probabilistic timing: attempts held between dispatch and the engine call (test hooks on the
// attempt's worker), cancellation delivery, concurrent shutdown, and injected failures in completion and finalization.
public sealed class SchedulerLifecycleRaceTests
{
    private const string Plugin = "a.5s.ps1";

    private static PluginId Id => PluginId.Create(Plugin);

    public static TheoryData<string> Supersessions => ["disable", "trust", "remove", "root"];

    // Starts a scheduler whose first attempt is held at the given hook until the returned gate is released.
    private static async Task<(SchedulerHarness Harness, Gate Hold)> StartHeldAsync(bool beforeDecision)
    {
        var hold = new Gate(observeCancellation: false);
        var hooks = beforeDecision
            ? new SchedulerTestHooks { AttemptStarting = _ => hold.Wait(CancellationToken.None) }
            : new SchedulerTestHooks { EngineCalling = _ => hold.Wait(CancellationToken.None) };
        var harness = new SchedulerHarness(hooks: hooks);
        harness.Ops.Add(Plugin);
        harness.Ops.Approve(Plugin);
        await harness.Scheduler.StartAsync(SchedulerHarness.Token);
        await hold.Entered;

        // The worker can reach the hook before the dispatching decision has published its snapshot.
        PluginSchedulerSnapshot dispatched = await harness.WaitForAsync(snapshot => snapshot.ActiveRuns == 1);
        Assert.Equal(PluginActivity.Running, dispatched.Find(Id)!.Activity);
        return (harness, hold);
    }

    private static void Supersede(SchedulerHarness harness, string how)
    {
        switch (how)
        {
            case "disable":
                Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.SetEnabled(Id, enabled: false));
                break;
            case "trust":
                harness.Ops.Revoke(Plugin);
                Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged(Id));
                break;
            case "remove":
                harness.Ops.Delete(Plugin);
                Assert.True(harness.Scheduler.RequestRescan());
                break;
            default:
                harness.Ops.RootProblem = new SchedulerDiagnostic(PluginDiagnosticSeverity.Warning, SchedulerDiagnosticCode.RootUnavailable, "gone");
                Assert.True(harness.Scheduler.RequestRescan());
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Supersessions))]
    public async Task AnAttemptSupersededBeforeItsLaunchDecisionNeverReachesTheEngine(string how)
    {
        (SchedulerHarness harness, Gate hold) = await StartHeldAsync(beforeDecision: true);
        await using (harness)
        {
            Supersede(harness, how);
            await harness.WaitForAsync(snapshot => snapshot.Find(Id) is not { Activity: PluginActivity.Running });

            hold.Release();
            await harness.SettleAsync();
            Assert.Equal(0, harness.Ops.EngineCalls);
            Assert.Empty(harness.Ops.Runs);
            Assert.Equal(0, harness.Snapshot.ActiveRuns);
            Assert.Empty(harness.Snapshot.CleanupBlocks);
            Assert.Equal(SchedulerLifecycle.Running, harness.Snapshot.Lifecycle);
            Assert.DoesNotContain(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code is SchedulerDiagnosticCode.SchedulerFaulted or SchedulerDiagnosticCode.CleanupIncomplete);
            if (harness.Snapshot.Find(Id) is { } plugin)
            {
                // A normal, not-started outcome: no engine status, no cleanup to resolve.
                PluginRunSummary run = plugin.LastRun!;
                Assert.Equal(PluginRunDisposition.Superseded, run.Disposition);
                Assert.Null(run.ExecutionStatus);
                Assert.Equal(ProcessCleanupStatus.NotStarted, run.CleanupStatus);
            }
        }
    }

    [Fact]
    public async Task ShutdownBeforeTheLaunchDecisionStartsNothingAndCompletes()
    {
        (SchedulerHarness harness, Gate hold) = await StartHeldAsync(beforeDecision: true);
        await using (harness)
        {
            Task<SchedulerStopResult> stop = harness.Scheduler.StopAsync(SchedulerHarness.Token);
            Assert.Equal(SchedulerLifecycle.Stopping, harness.Snapshot.Lifecycle);
            hold.Release();
            SchedulerStopResult result = await stop;

            Assert.True(result.IsComplete);
            Assert.Equal(SchedulerLifecycle.Stopped, result.Lifecycle);
            Assert.Equal(0, result.UnresolvedCleanupIncidents);
            Assert.Equal(0, harness.Ops.EngineCalls);
            Assert.Equal(ProcessCleanupStatus.NotStarted, harness.Plugin(Plugin).LastRun!.CleanupStatus);
        }
    }

    [Theory]
    [MemberData(nameof(Supersessions))]
    public async Task AnAttemptSupersededAfterItsLaunchDecisionReachesTheEngineAlreadyCancelled(string how)
    {
        (SchedulerHarness harness, Gate hold) = await StartHeldAsync(beforeDecision: false);
        await using (harness)
        {
            Supersede(harness, how);
            await harness.WaitForAsync(snapshot => snapshot.Find(Id) is not { Activity: PluginActivity.Running });

            // The token was cancelled under the lock that superseded the attempt, before the call that did so returned.
            hold.Release();
            await harness.SettleAsync();
            Assert.Equal(1, harness.Ops.EngineCalls);
            Assert.Equal(1, harness.Ops.CancelledEngineCalls);
            Assert.Empty(harness.Ops.Runs);
            Assert.Empty(harness.Snapshot.CleanupBlocks);
            Assert.Equal(SchedulerLifecycle.Running, harness.Snapshot.Lifecycle);
        }
    }

    [Fact]
    public async Task CancellationCallbacksNeverRunUnderTheSchedulerLock()
    {
        await using var harness = new SchedulerHarness();
        bool? lockFree = null;

        // A callback that needs the scheduler's lock on another thread: under the lock it could not finish in time.
        harness.Ops.CancellationCallback = () => lockFree = Task.Run(() => harness.Scheduler.RequestRescan()).Wait(TimeSpan.FromSeconds(10));
        await harness.StartAsync(Plugin);
        FakeRun run = harness.Ops.LastRun(Plugin);

        harness.Scheduler.SetEnabled(Id, enabled: false);
        Assert.True(run.CancellationRequested);
        await harness.SettleAsync();
        Assert.True(lockFree);
        Assert.True(run.IsCompleted);
    }

    [Fact]
    public async Task AFailingCancellationCallbackFaultsTheScheduler()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.CancellationCallback = static () => throw new InvalidOperationException("callback");
        await harness.StartAsync(Plugin);

        harness.Scheduler.NotifyTrustChanged(Id);
        PluginSchedulerSnapshot faulted = await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Faulted);
        SchedulerDiagnostic fault = Assert.Single(faulted.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted);
        Assert.Contains("cancellation", fault.Message, StringComparison.Ordinal);
        Assert.True(faulted.ExecutionHalted);

        // The other callbacks still ran: the run ended, and shutdown completes.
        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(result.IsComplete);
        Assert.Equal(0, result.UnresolvedCleanupIncidents);
    }

    [Fact]
    public async Task ConcurrentStopCallersShareOneTeardownAndNoneClaimsCompletionEarly()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("manual.ps1");
        FakeWatcher watcher = harness.Ops.Watchers[^1];
        var dispose = new Gate(observeCancellation: false);
        watcher.DisposeGate = dispose;

        Task<SchedulerStopResult> first = Task.Run(() => harness.Scheduler.StopAsync(SchedulerHarness.Token), SchedulerHarness.Token);
        await dispose.Entered;

        // A second caller that stops waiting at once while the first one's watcher disposal is still in progress.
        SchedulerStopResult early = await harness.Scheduler.StopAsync(new CancellationToken(canceled: true));
        Assert.False(early.IsComplete);
        Assert.True(early.TeardownOutstanding);
        Assert.Empty(early.OutstandingRuns);
        Assert.False(early.ReconciliationOutstanding);
        Assert.Equal(SchedulerLifecycle.Stopping, early.Lifecycle);
        Assert.Equal(SchedulerLifecycle.Stopping, harness.Snapshot.Lifecycle);
        Assert.False(watcher.Disposed);

        dispose.Release();
        SchedulerStopResult completed = await first;
        Assert.True(completed.IsComplete);
        Assert.False(completed.TeardownOutstanding);
        Assert.Equal(SchedulerLifecycle.Stopped, completed.Lifecycle);
        Assert.True(watcher.Disposed);
        Assert.True((await harness.Scheduler.StopAsync(SchedulerHarness.Token)).IsComplete);
        Assert.Single(harness.Ops.Watchers);
    }

    [Fact]
    public async Task TheShutdownBoundCoversABlockedTeardown()
    {
        var root = TrayWisp.Core.Trust.PluginRoot.Create(@"C:\plugins");
        var ops = new FakeSchedulerOperations(root);
        await using var scheduler = new PluginScheduler(ops, root, new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.FromMilliseconds(200) }, TimeProvider.System, hooks: null);
        await scheduler.StartAsync(SchedulerHarness.Token);
        await scheduler.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30), SchedulerHarness.Token);
        var dispose = new Gate(observeCancellation: false);
        ops.Watchers[^1].DisposeGate = dispose;

        var watch = System.Diagnostics.Stopwatch.StartNew();
        SchedulerStopResult result = await scheduler.StopAsync(SchedulerHarness.Token);
        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(10));
        Assert.False(result.IsComplete);
        Assert.True(result.TeardownOutstanding);
        Assert.Contains(scheduler.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.ShutdownIncomplete);

        dispose.Release();
        Assert.True((await scheduler.StopAsync(SchedulerHarness.Token)).IsComplete);
    }

    [Fact]
    public async Task AFailingWatcherDisposalIsReportedAndShutdownStillEnds()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("manual.ps1");
        harness.Ops.Watchers[^1].ThrowOnDispose = true;

        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(result.IsComplete);
        Assert.Equal(SchedulerLifecycle.Stopped, result.Lifecycle);
        SchedulerDiagnostic fault = Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted);
        Assert.Contains("disposal", fault.Message, StringComparison.Ordinal);
        Assert.True(harness.Snapshot.ExecutionHalted);
    }

    [Theory]
    [InlineData("Succeeded")]
    [InlineData("CleanupIncomplete")]
    public async Task AFailedCompletionFaultsTheSchedulerWithoutLosingTheRun(string kind)
    {
        RunOutcome outcome = Enum.Parse<RunOutcome>(kind);
        int injected = 0;
        var hooks = new SchedulerTestHooks
        {
            FaultPoint = point =>
            {
                if (point == SchedulerFaultPoint.Completion && Interlocked.Exchange(ref injected, 1) == 0)
                {
                    throw new InvalidOperationException("injected");
                }
            },
        };
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 1, ShutdownTimeout = TimeSpan.FromSeconds(5) }, hooks);
        await harness.StartAsync(Plugin, "b.1s.ps1");
        Assert.Equal(PluginActivity.Queued, harness.Plugin("b.1s.ps1").Activity);
        Assert.NotNull(harness.Plugin("b.1s.ps1").NextRefreshUtc);

        await harness.CompleteAsync(Plugin, outcome);
        PluginSchedulerSnapshot faulted = harness.Snapshot;
        Assert.Equal(SchedulerLifecycle.Faulted, faulted.Lifecycle);
        Assert.True(faulted.ExecutionHalted);
        Assert.Contains("completion", Assert.Single(faulted.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted).Message, StringComparison.Ordinal);
        Assert.Equal(0, faulted.ActiveRuns);
        Assert.Equal(outcome == RunOutcome.CleanupIncomplete ? 1 : 0, faulted.CleanupBlocks.Length);

        // A faulted scheduler does not look as if it will continue: nothing queued, no next refresh, nothing dispatched.
        Assert.All(faulted.Plugins, plugin =>
        {
            Assert.NotEqual(PluginActivity.Queued, plugin.Activity);
            Assert.Null(plugin.NextRefreshUtc);
            Assert.Null(plugin.Output);
        });
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Single(harness.Ops.Runs);

        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(result.IsComplete);
        Assert.Equal(outcome == RunOutcome.CleanupIncomplete ? 1 : 0, result.UnresolvedCleanupIncidents);
    }

    [Fact]
    public async Task ARunWhoseIncidentCannotBeRecordedAfterAFailedCompletionStaysOutstanding()
    {
        var hooks = new SchedulerTestHooks
        {
            MaxCleanupIncidents = 0,
            FaultPoint = point =>
            {
                if (point == SchedulerFaultPoint.Completion)
                {
                    throw new InvalidOperationException("injected");
                }
            },
        };
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.Zero }, hooks);
        await harness.StartAsync(Plugin);
        harness.Ops.LastRun(Plugin).Complete(RunOutcome.CleanupIncomplete);

        // Neither recorded nor forgotten: the attempt keeps its slot and is reported as outstanding.
        PluginSchedulerSnapshot faulted = await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Faulted);
        Assert.Equal(1, faulted.ActiveRuns);
        Assert.Empty(faulted.CleanupBlocks);
        Assert.True(faulted.ExecutionHalted);
        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.False(result.IsComplete);
        Assert.Equal([Id], result.OutstandingRuns);
    }

    [Fact]
    public async Task AFailureAfterTheCompletionHandlerIsStillObserved()
    {
        var hooks = new SchedulerTestHooks
        {
            FaultPoint = point =>
            {
                if (point is SchedulerFaultPoint.Completion or SchedulerFaultPoint.CompletionRecovery)
                {
                    throw new InvalidOperationException("injected");
                }
            },
        };
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.Zero }, hooks);
        await harness.StartAsync(Plugin);
        harness.Ops.LastRun(Plugin).Complete(RunOutcome.Succeeded);

        // The run's task faulted after its own handling: the scheduler faults and keeps the attempt registered.
        PluginSchedulerSnapshot faulted = await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Faulted);
        Assert.Contains("a run", Assert.Single(faulted.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted).Message, StringComparison.Ordinal);
        Assert.Equal(1, faulted.ActiveRuns);
        Assert.False((await harness.Scheduler.StopAsync(SchedulerHarness.Token)).IsComplete);
    }

    [Fact]
    public async Task AFailedReconciliationFinalizationFaultsAndEndsThePass()
    {
        int passes = 0;
        var hooks = new SchedulerTestHooks
        {
            FaultPoint = point =>
            {
                if (point == SchedulerFaultPoint.ReconciliationEnd && Interlocked.Increment(ref passes) == 2)
                {
                    throw new InvalidOperationException("injected");
                }
            },
        };
        await using var harness = new SchedulerHarness(hooks: hooks);
        await harness.StartAsync("manual.ps1");
        Assert.Equal(SchedulerLifecycle.Running, harness.Snapshot.Lifecycle);

        await harness.RescanAsync();
        PluginSchedulerSnapshot faulted = harness.Snapshot;
        Assert.Equal(SchedulerLifecycle.Faulted, faulted.Lifecycle);
        Assert.False(faulted.Discovery.Reconciling);
        Assert.Contains("reconciliation", Assert.Single(faulted.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted).Message, StringComparison.Ordinal);
        Assert.Equal(RefreshRequestStatus.ExecutionHalted, harness.Refresh("manual.ps1"));
        Assert.True((await harness.Scheduler.StopAsync(SchedulerHarness.Token)).IsComplete);
    }
}
