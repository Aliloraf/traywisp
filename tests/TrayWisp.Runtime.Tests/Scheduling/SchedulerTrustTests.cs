using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// Trust integration, expected approval generations, cancellation, stale results and output publication.
public sealed class SchedulerTrustTests
{
    private static PluginId Id(string value) => PluginId.Create(value);

    [Fact]
    public async Task APendingPluginIsNeverLaunched()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.Add("pending.1s.ps1");
        harness.Ops.Add("pending.ps1");
        await harness.StartAsync();

        Assert.Equal(RefreshRequestStatus.NotApproved, harness.Refresh("pending.ps1"));
        Assert.Equal(RefreshRequestStatus.NotApproved, harness.Refresh("pending.1s.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromMinutes(5));
        Assert.Empty(harness.Ops.Runs);
        Assert.Equal(0, harness.Ops.DeniedRuns);
        Assert.Equal(PluginEligibility.PendingApproval, harness.Plugin("pending.1s.ps1").Eligibility);
    }

    [Fact]
    public async Task AnApprovedPluginIsLaunchedUnderItsCurrentApprovalAndItsOutputPublished()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        PluginApprovalIdentity approval = harness.Ops.ApprovalOf("a.5s.ps1")!;
        Assert.Equal(approval, harness.Ops.LastRun("a.5s.ps1").Approval);

        harness.Ops.LastRun("a.5s.ps1").Succeed("hello\n---\nitem");
        await harness.SettleAsync();
        ScheduledPlugin plugin = harness.Plugin("a.5s.ps1");
        PublishedPluginOutput output = Assert.IsType<PublishedPluginOutput>(plugin.Output);
        Assert.Equal("hello", output.Output.Titles[0].Item.Text);
        Assert.Equal(approval, output.Approval);
        Assert.Equal(plugin.LastRun!.RunId, output.RunId);
        Assert.Equal(PluginRunDisposition.Published, plugin.LastRun.Disposition);
        Assert.Equal(1, harness.Ops.ApprovalChecks);
        Assert.True(harness.Snapshot.RetainedOutputCost > 0);
    }

    [Fact]
    public async Task EmptySuccessfulOutputReplacesEarlierOutputAndAFailureClearsIt()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.1s.ps1");
        await harness.CompleteAsync("a.1s.ps1");
        Assert.NotEmpty(harness.Plugin("a.1s.ps1").Output!.Output.Titles);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        await harness.CompleteAsync("a.1s.ps1", RunOutcome.Empty);
        PublishedPluginOutput empty = harness.Plugin("a.1s.ps1").Output!;
        Assert.Empty(empty.Output.Titles);
        Assert.Empty(empty.Output.Items);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        await harness.CompleteAsync("a.1s.ps1", RunOutcome.Failed);
        Assert.Null(harness.Plugin("a.1s.ps1").Output);
        Assert.Equal(PluginRunDisposition.Failed, harness.Plugin("a.1s.ps1").LastRun!.Disposition);
        Assert.Equal(PluginExecutionStatus.NonZeroExitCode, harness.Plugin("a.1s.ps1").LastRun!.ExecutionStatus);
        Assert.Equal(0, harness.Snapshot.RetainedOutputCost);
    }

    [Fact]
    public async Task TheAggregateOutputBudgetIsEnforced()
    {
        // Each "ok" output costs 1024 + 128 for its title + 36 for its label = 1,188: two fit in 3,000, a third does not.
        long each = PluginSchedulingPolicy.EstimateRetainedOutputCost(TrayWisp.Core.Output.PluginOutputParser.Parse("ok").Output);
        Assert.Equal(1_188, each);
        await using var harness = new SchedulerHarness(hooks: new SchedulerTestHooks { MaxRetainedOutputCost = 3_000 });
        await harness.StartAsync("a.1s.ps1", "b.1s.ps1", "c.1s.ps1");
        await harness.CompleteAsync("a.1s.ps1");
        await harness.CompleteAsync("b.1s.ps1");
        await harness.CompleteAsync("c.1s.ps1");

        Assert.Equal(2 * each, harness.Snapshot.RetainedOutputCost);
        Assert.Null(harness.Plugin("c.1s.ps1").Output);
        Assert.Equal(PluginRunDisposition.NotPublished, harness.Plugin("c.1s.ps1").LastRun!.Disposition);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.OutputBudgetExceeded);

        // Replacing a plugin's own output does not count it twice.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        await harness.CompleteAsync("a.1s.ps1");
        Assert.NotNull(harness.Plugin("a.1s.ps1").Output);
        Assert.Equal(2 * each, harness.Snapshot.RetainedOutputCost);
    }

    [Fact]
    public async Task ArgumentHeavyOutputIsAccountedForThroughReplacementInvalidationAndExhaustion()
    {
        string arguments = string.Join(' ', Enumerable.Range(1, TrayWisp.Core.Output.PluginOutputParser.MaxCommandArguments).Select(i => $"param{i}=value{i}"));
        string heavy = "t\n---\n" + string.Concat(Enumerable.Repeat($"x | shell=tool.exe {arguments}\n", 200));
        long heavyCost = PluginSchedulingPolicy.EstimateRetainedOutputCost(TrayWisp.Core.Output.PluginOutputParser.Parse(heavy).Output);
        long lightCost = PluginSchedulingPolicy.EstimateRetainedOutputCost(TrayWisp.Core.Output.PluginOutputParser.Parse("ok").Output);
        Assert.True(heavyCost > 200 * 32 * 32, $"{heavyCost}");

        // Room for one heavy output and one light one, not for two heavy ones.
        await using var harness = new SchedulerHarness(hooks: new SchedulerTestHooks { MaxRetainedOutputCost = heavyCost + (2 * lightCost) });
        await harness.StartAsync("a.1s.ps1", "b.1s.ps1");
        harness.Ops.LastRun("a.1s.ps1").Succeed(heavy);
        await harness.SettleAsync();
        Assert.Equal(heavyCost, harness.Snapshot.RetainedOutputCost);

        // Exhaustion: b's heavy output does not fit; its light output does.
        harness.Ops.LastRun("b.1s.ps1").Succeed(heavy);
        await harness.SettleAsync();
        Assert.Null(harness.Plugin("b.1s.ps1").Output);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.OutputBudgetExceeded);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        harness.Ops.LastRun("b.1s.ps1").Succeed("ok");
        await harness.SettleAsync();
        Assert.Equal(heavyCost + lightCost, harness.Snapshot.RetainedOutputCost);

        // Replacement: a's heavy output replaced by a light one releases the difference.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        harness.Ops.LastRun("a.1s.ps1").Succeed("ok");
        await harness.SettleAsync();
        Assert.Equal(2 * lightCost, harness.Snapshot.RetainedOutputCost);

        // Invalidation releases it entirely.
        harness.Scheduler.NotifyTrustChanged();
        Assert.Equal(0, harness.Snapshot.RetainedOutputCost);
        Assert.All(harness.Snapshot.Plugins, plugin => Assert.Null(plugin.Output));
    }

    [Fact]
    public async Task RevocationDuringARunCancelsItAndItsLateSuccessIsNotPublished()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync("a.5s.ps1");
        FakeRun run = harness.Ops.LastRun("a.5s.ps1");

        harness.Ops.Revoke("a.5s.ps1");
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged(Id("a.5s.ps1")));
        Assert.True(run.CancellationRequested);
        Assert.Equal(PluginActivity.Cancelling, harness.Plugin("a.5s.ps1").Activity);
        await harness.SettleAsync();
        Assert.Equal(PluginScheduleStatus.PendingApproval, harness.Plugin("a.5s.ps1").Status);

        // The engine has not stopped yet: the slot stays occupied until it does.
        Assert.Equal(1, harness.Snapshot.ActiveRuns);
        run.Succeed();
        await harness.SettleAsync();
        Assert.Equal(0, harness.Snapshot.ActiveRuns);
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        Assert.Equal(PluginRunDisposition.Superseded, harness.Plugin("a.5s.ps1").LastRun!.Disposition);
        Assert.Contains(harness.Plugin("a.5s.ps1").LastRun!.SchedulerDiagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.OutputNotPublished);
        Assert.Equal(0, harness.Ops.ApprovalChecks);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Single(harness.Ops.Runs);
    }

    [Fact]
    public async Task AReapprovalCancelsTheRunOfTheOldGenerationAndRunsUnderTheNewOne()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        FakeRun first = harness.Ops.LastRun("a.5s.ps1");

        // Revoked and approved again (same bytes, new generation) by another process: found by periodic reconciliation.
        harness.Ops.Revoke("a.5s.ps1");
        PluginApprovalIdentity second = harness.Ops.Approve("a.5s.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.True(first.CancellationRequested);
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));
        await harness.AdvanceAsync(PluginSchedulingPolicy.MinimumRelaunchDelay);
        Assert.Equal(2, harness.Ops.Launches("a.5s.ps1"));
        Assert.Equal(second, harness.Ops.LastRun("a.5s.ps1").Approval);
        Assert.Equal(1, harness.Ops.MaxConcurrentPerKey);

        await harness.CompleteAsync("a.5s.ps1");
        Assert.Equal(second, harness.Plugin("a.5s.ps1").Output!.Approval);
    }

    [Fact]
    public async Task ARunSelectedUnderAnOldGenerationIsNeverLaunchedUnderANewerOne()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.ps1");
        PluginApprovalIdentity first = harness.Ops.ApprovalOf("a.ps1")!;

        // The scheduler still believes in the first approval; the store now holds another generation.
        harness.Ops.Revoke("a.ps1");
        PluginApprovalIdentity second = harness.Ops.Approve("a.ps1");
        Assert.Equal(RefreshRequestStatus.Accepted, harness.Refresh("a.ps1"));
        await harness.SettleAsync();

        // The launch-time check required the first generation and denied it; nothing ran under the second.
        Assert.Equal(1, harness.Ops.DeniedRuns);
        Assert.Empty(harness.Ops.Runs);
        ScheduledPlugin plugin = harness.Plugin("a.ps1");
        Assert.Equal(PluginExecutionStatus.TrustDenied, plugin.LastRun!.ExecutionStatus);
        Assert.Equal(PluginTrustStatus.StaleApproval, plugin.LastRun.TrustStatus);

        // The scheduler checked again and found the second approval; a new request runs under it.
        Assert.Equal(PluginTrustStatus.Approved, plugin.ObservedTrustStatus);
        Assert.Equal(RefreshRequestStatus.Accepted, harness.Refresh("a.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(second, harness.Ops.LastRun("a.ps1").Approval);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task ExternalRevocationIsFoundByPeriodicReconciliationEvenForManualPlugins()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("manual.ps1");
        harness.Refresh("manual.ps1");
        await harness.SettleAsync();
        await harness.CompleteAsync("manual.ps1");
        Assert.NotNull(harness.Plugin("manual.ps1").Output);

        // Revoked in another process: no notification. Found within one reconciliation interval.
        harness.Ops.Revoke("manual.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.NotNull(harness.Plugin("manual.ps1").Output);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Null(harness.Plugin("manual.ps1").Output);
        Assert.Equal(PluginScheduleStatus.PendingApproval, harness.Plugin("manual.ps1").Status);
    }

    [Fact]
    public async Task AnUnavailableStoreNeverReusesAnEarlierAuthorization()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");

        harness.Ops.StoreUnavailable = true;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        ScheduledPlugin plugin = harness.Plugin("a.5s.ps1");
        Assert.Equal(PluginScheduleStatus.Unavailable, plugin.Status);
        Assert.Equal(PluginTrustStatus.StoreUnavailable, plugin.ObservedTrustStatus);
        Assert.Null(plugin.Output);
        Assert.Equal(RefreshRequestStatus.Unavailable, harness.Refresh("a.5s.ps1"));
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));

        // Available again: approved again by the next check, and due at once.
        harness.Ops.StoreUnavailable = false;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, harness.Ops.Launches("a.5s.ps1"));
    }

    [Fact]
    public async Task ADisabledPluginIsNeverLaunchedAndEnablingMakesItDueAtOnce()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.Add("a.1m.ps1");
        harness.Ops.Approve("a.1m.ps1");
        await harness.StartAsync();
        await harness.CompleteAsync("a.1m.ps1");

        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.SetEnabled(Id("a.1m.ps1"), enabled: false));
        Assert.Equal(PluginCommandStatus.Unchanged, harness.Scheduler.SetEnabled(Id("A.1M.PS1"), enabled: false));
        Assert.Equal(PluginCommandStatus.UnknownPlugin, harness.Scheduler.SetEnabled(Id("b.ps1"), enabled: false));
        ScheduledPlugin disabled = harness.Plugin("a.1m.ps1");
        Assert.Equal(PluginScheduleStatus.Disabled, disabled.Status);
        Assert.Null(disabled.Output);
        Assert.Null(disabled.NextRefreshUtc);
        Assert.Equal(RefreshRequestStatus.Disabled, harness.Refresh("a.1m.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromMinutes(10));
        Assert.Equal(1, harness.Ops.Launches("a.1m.ps1"));

        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.SetEnabled(Id("a.1m.ps1"), enabled: true));
        await harness.SettleAsync();
        Assert.Equal(2, harness.Ops.Launches("a.1m.ps1"));
    }

    [Fact]
    public async Task DisablingDuringARunCancelsItAndALateCompletionDoesNotReenableIt()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync("a.5s.ps1");
        FakeRun run = harness.Ops.LastRun("a.5s.ps1");

        harness.Scheduler.SetEnabled(Id("a.5s.ps1"), enabled: false);
        Assert.True(run.CancellationRequested);
        run.Succeed();
        await harness.SettleAsync();
        ScheduledPlugin plugin = harness.Plugin("a.5s.ps1");
        Assert.False(plugin.IsEnabled);
        Assert.Null(plugin.Output);
        Assert.Null(plugin.NextRefreshUtc);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Single(harness.Ops.Runs);
    }

    [Fact]
    public async Task RevocationBetweenExecutionAndPublicationPreventsPublication()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        harness.Ops.BeforeApprovalCheck = _ => harness.Ops.Revoke("a.5s.ps1");

        await harness.CompleteAsync("a.5s.ps1");
        ScheduledPlugin plugin = harness.Plugin("a.5s.ps1");
        Assert.Null(plugin.Output);
        Assert.Equal(PluginRunDisposition.NotPublished, plugin.LastRun!.Disposition);
        Assert.Equal(PluginScheduleStatus.PendingApproval, plugin.Status);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.OutputNotPublished);
    }

    [Fact]
    public async Task AContentChangeBetweenExecutionAndPublicationPreventsPublicationAndInvalidates()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        harness.Ops.BeforeApprovalCheck = _ => harness.Ops.ModifyContentOnly("a.5s.ps1");

        await harness.CompleteAsync("a.5s.ps1");
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        Assert.Equal(PluginTrustStatus.ContentChanged, harness.Plugin("a.5s.ps1").ObservedTrustStatus);
        Assert.Null(harness.Ops.ApprovalOf("a.5s.ps1"));
    }

    [Fact]
    public async Task AnInvalidationDuringTheOutputCheckWins()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        var gate = new Gate();
        harness.Ops.ApprovalCheckGate = gate;
        harness.Ops.LastRun("a.5s.ps1").Succeed();
        await gate.Entered;

        // The output check (which will approve) is in progress; the host disables the plugin meanwhile.
        harness.Scheduler.SetEnabled(Id("a.5s.ps1"), enabled: false);
        harness.Ops.ApprovalCheckGate = null;
        gate.Release();
        await harness.SettleAsync();
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        Assert.Equal(PluginRunDisposition.Superseded, harness.Plugin("a.5s.ps1").LastRun!.Disposition);
    }

    [Fact]
    public async Task OutputOfAPluginThatBecameUncertainDuringItsRunIsNotPublished()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");

        // An incomplete scan misses the plugin while it runs (its file is still there for the trust check).
        var gate = new Gate();
        harness.Ops.DiscoverGate = gate;
        harness.Ops.ScanComplete = false;
        harness.Ops.Delete("a.5s.ps1");
        harness.Scheduler.RequestRescan();
        await gate.Entered;
        harness.Ops.Add("a.5s.ps1");
        harness.Ops.DiscoverGate = null;
        gate.Release();
        await harness.SettleAsync();
        Assert.Equal(PluginPresence.Uncertain, harness.Plugin("a.5s.ps1").Presence);
        Assert.False(harness.Ops.LastRun("a.5s.ps1").CancellationRequested);

        await harness.CompleteAsync("a.5s.ps1");
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        Assert.Equal(PluginRunDisposition.NotPublished, harness.Plugin("a.5s.ps1").LastRun!.Disposition);
    }

    [Fact]
    public async Task ARemovedAndReappearedPluginWaitsForItsOldRunAndNeverReceivesItsResult()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync("a.5s.ps1");
        FakeRun old = harness.Ops.LastRun("a.5s.ps1");

        harness.Ops.Delete("a.5s.ps1");
        await harness.RescanAsync();
        Assert.True(old.CancellationRequested);
        Assert.Empty(harness.Snapshot.Plugins);
        Assert.Equal([Id("a.5s.ps1")], harness.Snapshot.RetiredRuns);
        Assert.Equal(1, harness.Snapshot.ActiveRuns);

        // Back with the same identity: tracked as a new plugin, but not launched while the old run is outstanding.
        harness.Ops.Add("a.5s.ps1");
        await harness.RescanAsync();
        Assert.Equal(PluginEligibility.Eligible, harness.Plugin("a.5s.ps1").Eligibility);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Single(harness.Ops.Runs);

        old.Succeed();
        await harness.SettleAsync();
        Assert.Null(harness.Plugin("a.5s.ps1").LastRun);
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        await harness.AdvanceAsync(PluginSchedulingPolicy.MinimumRelaunchDelay);
        Assert.Equal(2, harness.Ops.Runs.Count);
        Assert.Empty(harness.Snapshot.RetiredRuns);
    }

    [Fact]
    public async Task SnapshotsAreImmutableAndRevisionsIncrease()
    {
        await using var harness = new SchedulerHarness();
        PluginSchedulerSnapshot initial = harness.Snapshot;
        Assert.Equal(SchedulerLifecycle.NotStarted, initial.Lifecycle);
        await harness.StartAsync("a.5s.ps1");
        PluginSchedulerSnapshot running = harness.Snapshot;
        Assert.True(running.Revision > initial.Revision);
        Assert.Empty(initial.Plugins);

        await harness.CompleteAsync("a.5s.ps1");
        PluginSchedulerSnapshot published = harness.Snapshot;
        Assert.True(published.Revision > running.Revision);
        Assert.Null(running.Find(Id("a.5s.ps1"))!.Output);
        Assert.NotNull(published.Find(Id("a.5s.ps1"))!.Output);

        // A snapshot a consumer holds is never changed afterwards: it still shows the output after a revocation.
        harness.Scheduler.NotifyTrustChanged(Id("a.5s.ps1"));
        Assert.NotNull(published.Find(Id("a.5s.ps1"))!.Output);
        Assert.Null(harness.Snapshot.Find(Id("a.5s.ps1"))!.Output);

        // Waiting returns a newer snapshot; an already newer one is returned at once.
        Assert.True((await harness.Scheduler.WaitForChangeAsync(published.Revision, SchedulerHarness.Token)).Revision > published.Revision);
        await harness.SettleAsync();
        Task<PluginSchedulerSnapshot> next = harness.Scheduler.WaitForChangeAsync(harness.Snapshot.Revision, SchedulerHarness.Token);
        Assert.False(next.IsCompleted);
        harness.Scheduler.SetEnabled(Id("a.5s.ps1"), enabled: false);
        Assert.Equal(harness.Snapshot.Revision, (await next.WaitAsync(TimeSpan.FromSeconds(10), SchedulerHarness.Token)).Revision);
    }

    [Fact]
    public async Task NothingChangesWhenNothingHappens()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("manual.ps1");
        long revision = harness.Snapshot.Revision;

        // Time passing and irrelevant notifications publish nothing new.
        for (int i = 0; i < 100; i++)
        {
            harness.Ops.Watchers[^1].Raise(WatcherChangeKind.Changed, "cache.json");
        }

        await harness.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.Equal(revision, harness.Snapshot.Revision);
    }

    [Fact]
    public async Task WaitingConsumersCannotDisturbTheScheduler()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.1s.ps1");

        // A consumer whose continuation throws, one that blocks, and one that cancels: none of them runs on the
        // scheduler's threads or under its lock, and none holds anything back.
        Task throwing = harness.Scheduler.WaitForChangeAsync(harness.Snapshot.Revision, SchedulerHarness.Token).ContinueWith(_ => throw new InvalidOperationException("consumer"), TaskScheduler.Default);
        using var release = new ManualResetEventSlim();
        Task blocking = harness.Scheduler.WaitForChangeAsync(harness.Snapshot.Revision, SchedulerHarness.Token).ContinueWith(_ => release.Wait(TimeSpan.FromSeconds(30)), TaskScheduler.Default);
        using var cancel = new CancellationTokenSource();
        Task<PluginSchedulerSnapshot> cancelled = harness.Scheduler.WaitForChangeAsync(harness.Snapshot.Revision, cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        for (int i = 0; i < 5; i++)
        {
            await harness.CompleteAsync("a.1s.ps1");
            await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(6, harness.Ops.Launches("a.1s.ps1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => throwing);
        release.Set();
        await blocking;
        Assert.Equal(SchedulerLifecycle.Running, harness.Snapshot.Lifecycle);
    }

    [Fact]
    public async Task ConcurrentRequestsKeepTheInvariants()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 3 });
        harness.Ops.AutoComplete = run => run.Id.Value.StartsWith('f') ? RunOutcome.Failed : RunOutcome.Succeeded;
        string[] ids = [.. Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? "s" : "f") + i + ".250ms.ps1")];
        await harness.StartAsync(ids);

        Task[] workers =
        [
            .. Enumerable.Range(0, 8).Select(worker => Task.Run(
                () =>
                {
                    for (int i = 0; i < 300; i++)
                    {
                        PluginId id = Id(ids[(worker + i) % ids.Length]);
                        switch (i % 5)
                        {
                            case 0: harness.Scheduler.RequestRefresh(id); break;
                            case 1: harness.Scheduler.RequestRescan(); break;
                            case 2: harness.Scheduler.SetEnabled(id, enabled: i % 3 != 0); break;
                            case 3: harness.Scheduler.NotifyTrustChanged(id); break;
                            default: _ = harness.Snapshot.Plugins.Length; break;
                        }
                    }
                },
                SchedulerHarness.Token)),
            Task.Run(
                async () =>
                {
                    for (int i = 0; i < 50; i++)
                    {
                        await harness.AdvanceAsync(TimeSpan.FromMilliseconds(100));
                    }
                },
                SchedulerHarness.Token),
        ];
        await Task.WhenAll(workers);

        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(result.IsComplete);
        Assert.Equal(SchedulerLifecycle.Stopped, harness.Snapshot.Lifecycle);
        Assert.InRange(harness.Ops.MaxConcurrentRuns, 1, 3);
        Assert.Equal(1, harness.Ops.MaxConcurrentPerKey);
        Assert.All(harness.Snapshot.Plugins, plugin => Assert.Null(plugin.Output));
    }
}
