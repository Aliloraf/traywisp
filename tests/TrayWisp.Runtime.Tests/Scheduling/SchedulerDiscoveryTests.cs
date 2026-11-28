using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// Discovery reconciliation and file system notifications, with fake operations and a manual clock.
public sealed class SchedulerDiscoveryTests
{
    [Fact]
    public async Task TheInitialScanTracksEveryDiscoveredPluginAndChecksItsTrust()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.Add("pending.5s.ps1");
        harness.Ops.Add("script.sh");
        await harness.StartAsync("approved.5s.ps1");

        PluginSchedulerSnapshot snapshot = harness.Snapshot;
        Assert.Equal(SchedulerLifecycle.Running, snapshot.Lifecycle);
        Assert.Equal(["approved.5s.ps1", "pending.5s.ps1", "script.sh"], snapshot.Plugins.Select(plugin => plugin.Id.Value));
        Assert.Equal(1, snapshot.Discovery.CompletedScans);
        Assert.True(snapshot.Discovery.LastScanComplete);
        Assert.True(snapshot.Discovery.RootAvailable);
        Assert.True(snapshot.Discovery.WatcherActive);
        Assert.Equal(PluginScheduleStatus.Running, harness.Plugin("approved.5s.ps1").Status);
        Assert.Equal(PluginScheduleStatus.PendingApproval, harness.Plugin("pending.5s.ps1").Status);
        Assert.Equal(PluginTrustStatus.PendingApproval, harness.Plugin("pending.5s.ps1").ObservedTrustStatus);

        // Bash and batch are tracked and shown, never trust-checked or launched.
        Assert.Equal(PluginScheduleStatus.UnsupportedLanguage, harness.Plugin("script.sh").Status);
        Assert.Null(harness.Plugin("script.sh").ObservedTrustStatus);
        Assert.Equal(RefreshRequestStatus.UnsupportedLanguage, harness.Refresh("script.sh"));
        Assert.Equal(2, harness.Ops.TrustChecks);
    }

    [Fact]
    public async Task AddedPluginsAreScheduledAndRemovedOnesForgottenOnlyAfterACompleteScan()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");

        harness.Ops.Add("b.5s.ps1");
        harness.Ops.Approve("b.5s.ps1");
        await harness.RescanAsync();
        Assert.Equal(1, harness.Ops.Launches("b.5s.ps1"));

        harness.Ops.Delete("a.5s.ps1");
        await harness.RescanAsync();
        Assert.Null(harness.Snapshot.Find(PluginId.Create("a.5s.ps1")));

        // The approval of a removed plugin is never touched.
        Assert.NotNull(harness.Ops.ApprovalOf("a.5s.ps1"));
    }

    [Fact]
    public async Task AnIncompleteScanNeitherRemovesNorRevokesAndSuspendsLaunches()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1", "b.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");
        await harness.CompleteAsync("b.5s.ps1");
        PluginApprovalIdentity approval = harness.Ops.ApprovalOf("a.5s.ps1")!;

        // "a" is missing from an incomplete scan (for example locked while an editor saves it), but still exists.
        harness.Ops.ScanComplete = false;
        harness.Ops.Delete("a.5s.ps1");
        await harness.RescanAsync();
        ScheduledPlugin a = harness.Plugin("a.5s.ps1");
        Assert.Equal(PluginPresence.Uncertain, a.Presence);
        Assert.Equal(PluginScheduleStatus.Uncertain, a.Status);
        Assert.Equal("a.5s.ps1", a.Descriptor.Id.Value);
        Assert.False(harness.Snapshot.Discovery.LastScanComplete);
        Assert.Equal(1, harness.Snapshot.Discovery.UncertainPlugins);
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.DiscoveryIncomplete);
        Assert.Equal(approval, harness.Ops.ApprovalOf("a.5s.ps1"));

        // Its targeted trust check could not find it: its output is withdrawn, but it is not removed.
        Assert.Null(a.Output);
        Assert.Equal(PluginTrustStatus.PluginUnavailable, a.ObservedTrustStatus);
        Assert.Equal(RefreshRequestStatus.Uncertain, harness.Refresh("a.5s.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));
        Assert.NotNull(harness.Plugin("b.5s.ps1").Output);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        await harness.CompleteAsync("b.5s.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.DiscoveryIncomplete);

        // The incomplete scan is retried; once a scan lists it again it is checked and runs again.
        harness.Ops.Add("a.5s.ps1");
        harness.Ops.ScanComplete = true;
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(PluginPresence.Present, harness.Plugin("a.5s.ps1").Presence);
        Assert.Equal(2, harness.Ops.Launches("a.5s.ps1"));
    }

    [Fact]
    public async Task AnUncertainPluginWhoseTrustIsConfirmedKeepsItsOutput()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");

        // Missing from an incomplete scan (its listing was taken while the file was briefly away), but the authoritative
        // check afterwards finds the approved bytes.
        var gate = new Gate();
        harness.Ops.DiscoverGate = gate;
        harness.Ops.ScanComplete = false;
        harness.Ops.Delete("a.5s.ps1");
        harness.Scheduler.RequestRescan();
        await gate.Entered;
        harness.Ops.Add("a.5s.ps1");
        gate.Release();
        await harness.SettleAsync();

        ScheduledPlugin a = harness.Plugin("a.5s.ps1");
        Assert.Equal(PluginPresence.Uncertain, a.Presence);
        Assert.Equal(PluginTrustStatus.Approved, a.ObservedTrustStatus);
        Assert.NotNull(a.Output);
    }

    [Fact]
    public async Task AModifiedPluginLosesItsApprovalThroughTheTrustService()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");
        Assert.NotNull(harness.Plugin("a.5s.ps1").Output);

        harness.Ops.Modify("a.5s.ps1");
        await harness.RescanAsync();
        ScheduledPlugin a = harness.Plugin("a.5s.ps1");
        Assert.Equal(PluginTrustStatus.ContentChanged, a.ObservedTrustStatus);
        Assert.Equal(PluginScheduleStatus.PendingApproval, a.Status);
        Assert.Null(a.Output);
        Assert.Null(harness.Ops.ApprovalOf("a.5s.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));
    }

    [Fact]
    public async Task AContentChangeWithoutAMetadataChangeIsFoundByTheFingerprint()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");

        // Same descriptor, same length and timestamp: only the hash tells. The watcher hint requests a targeted check.
        harness.Ops.ModifyContentOnly("a.5s.ps1");
        harness.Ops.Watchers[^1].Raise(WatcherChangeKind.Changed, "a.5s.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(PluginTrustStatus.ContentChanged, harness.Plugin("a.5s.ps1").ObservedTrustStatus);
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
    }

    [Fact]
    public async Task ARescanIsDiscoveryOnlyAndARequestedReconciliationRevalidatesTrust()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");
        int checks = harness.Ops.TrustChecks;

        // An edit that leaves the metadata unchanged, without a notification: a rescan finds nothing to change.
        harness.Ops.ModifyContentOnly("a.5s.ps1");
        await harness.RescanAsync();
        Assert.Equal(checks, harness.Ops.TrustChecks);
        Assert.NotNull(harness.Plugin("a.5s.ps1").Output);

        // A requested reconciliation checks every plugin at once, without waiting for the periodic interval.
        Assert.True(harness.Scheduler.RequestReconciliation());
        await harness.SettleAsync();
        Assert.Equal(checks + 1, harness.Ops.TrustChecks);
        Assert.Equal(PluginTrustStatus.ContentChanged, harness.Plugin("a.5s.ps1").ObservedTrustStatus);
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        Assert.Null(harness.Ops.ApprovalOf("a.5s.ps1"));

        // Not running: rejected.
        await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.False(harness.Scheduler.RequestReconciliation());
    }

    [Fact]
    public async Task ARequestedReconciliationKeepsConfirmedOutputAndRuns()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync("a.5s.ps1", "b.ps1");
        harness.Refresh("b.ps1");
        await harness.SettleAsync();
        await harness.CompleteAsync("b.ps1");
        long revision = harness.Plugin("b.ps1").LifecycleRevision;

        Assert.True(harness.Scheduler.RequestReconciliation());
        await harness.SettleAsync();
        Assert.NotNull(harness.Plugin("b.ps1").Output);
        Assert.Equal(revision, harness.Plugin("b.ps1").LifecycleRevision);
        Assert.False(harness.Ops.LastRun("a.5s.ps1").CancellationRequested);
    }

    [Fact]
    public async Task ARenamedPluginIsANewPluginWithoutTheOldApprovalOrOutput()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("clock.5s.ps1");
        await harness.CompleteAsync("clock.5s.ps1");

        // Also how the refresh interval changes: it is part of the identity.
        harness.Ops.Rename("clock.5s.ps1", "clock.10s.ps1");
        await harness.RescanAsync();
        Assert.Null(harness.Snapshot.Find(PluginId.Create("clock.5s.ps1")));
        ScheduledPlugin renamed = harness.Plugin("clock.10s.ps1");
        Assert.Equal(PluginScheduleStatus.PendingApproval, renamed.Status);
        Assert.Null(renamed.Output);
        Assert.Null(renamed.LastRun);
        Assert.Equal(TimeSpan.FromSeconds(10), renamed.RefreshInterval!.Duration);

        harness.Ops.Approve("clock.10s.ps1");
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged(PluginId.Create("clock.10s.ps1")));
        await harness.SettleAsync();
        Assert.Equal(1, harness.Ops.Launches("clock.10s.ps1"));
    }

    [Fact]
    public async Task ACaseOnlyRenameKeepsTheIdentityApprovalAndOutput()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("Clock.5s.ps1");
        await harness.CompleteAsync("Clock.5s.ps1");
        long revision = harness.Plugin("clock.5s.ps1").LifecycleRevision;

        harness.Ops.Rename("Clock.5s.ps1", "clock.5s.ps1");
        await harness.RescanAsync();
        ScheduledPlugin plugin = harness.Plugin("CLOCK.5S.PS1");
        Assert.Equal("clock.5s.ps1", plugin.Id.Value);
        Assert.Equal(revision, plugin.LifecycleRevision);
        Assert.NotNull(plugin.Output);
        Assert.Single(harness.Snapshot.Plugins);
    }

    [Fact]
    public async Task AMetadataChangeWithdrawsOutputAndCancelsAtOnce()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync("a.5s.ps1");
        FakeRun run = harness.Ops.LastRun("a.5s.ps1");

        // Ordinarily the lease blocks edits during a run; a scan that sees different metadata cancels conservatively.
        harness.Ops.Modify("a.5s.ps1");
        await harness.RescanAsync();
        Assert.True(run.CancellationRequested);
        Assert.Equal(PluginActivity.Cancelling, harness.Plugin("a.5s.ps1").Activity);
        run.Succeed();
        await harness.SettleAsync();
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        Assert.Equal(PluginRunDisposition.Superseded, harness.Plugin("a.5s.ps1").LastRun!.Disposition);
    }

    [Fact]
    public async Task ACollidingOrUnsafeEntryIsExcluded()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");

        // Discovery excludes colliding names from a complete result: the plugin is gone, and its run cancelled.
        harness.Ops.Delete("a.5s.ps1");
        await harness.RescanAsync();
        Assert.Empty(harness.Snapshot.Plugins);

        // An entry the trust check finds unsafe (a link, an alias, a collision found by the targeted check) is not
        // launched, shows as unavailable, and loses its output.
        harness.Ops.Add("b.5s.ps1");
        harness.Ops.Approve("b.5s.ps1");
        await harness.RescanAsync();
        await harness.CompleteAsync("b.5s.ps1");
        harness.Ops.MarkUnsafe("b.5s.ps1");
        harness.Scheduler.NotifyTrustChanged(PluginId.Create("b.5s.ps1"));
        await harness.SettleAsync();
        Assert.Equal(PluginScheduleStatus.Unavailable, harness.Plugin("b.5s.ps1").Status);
        Assert.Equal(PluginTrustStatus.UnsafePath, harness.Plugin("b.5s.ps1").ObservedTrustStatus);
        Assert.Null(harness.Plugin("b.5s.ps1").Output);
        Assert.Equal(RefreshRequestStatus.Unavailable, harness.Refresh("b.5s.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, harness.Ops.Launches("b.5s.ps1"));
    }

    [Fact]
    public async Task TheRootCanDisappearAndComeBack()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");
        FakeWatcher first = harness.Ops.Watchers[^1];

        harness.Ops.RootProblem = new SchedulerDiagnostic(PluginDiagnosticSeverity.Warning, SchedulerDiagnosticCode.RootUnavailable, "gone");
        harness.Ops.Delete("a.5s.ps1");
        await harness.RescanAsync();
        Assert.False(harness.Snapshot.Discovery.RootAvailable);
        Assert.False(harness.Snapshot.Discovery.WatcherActive);
        Assert.True(first.Disposed);
        Assert.Equal(PluginPresence.Uncertain, harness.Plugin("a.5s.ps1").Presence);
        Assert.Null(harness.Plugin("a.5s.ps1").Output);
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.RootUnavailable);

        // Retried every 5 s without repeating the diagnostic.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(20));
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.RootUnavailable);

        harness.Ops.RootProblem = null;
        harness.Ops.Add("a.5s.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.True(harness.Snapshot.Discovery.RootAvailable);
        Assert.True(harness.Snapshot.Discovery.WatcherActive);
        Assert.Equal(2, harness.Ops.Watchers.Count);
        Assert.Equal(PluginPresence.Present, harness.Plugin("a.5s.ps1").Presence);
        Assert.Equal(2, harness.Ops.Launches("a.5s.ps1"));
    }

    [Fact]
    public async Task AReplacedRootRestartsWatchingAndChecksEveryPlugin()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1", "b.ps1");
        int checks = harness.Ops.TrustChecks;

        harness.Ops.Identity = new RootIdentity(1, 2, 3);
        await harness.RescanAsync();
        Assert.Equal(2, harness.Ops.Watchers.Count);
        Assert.True(harness.Ops.Watchers[0].Disposed);
        Assert.Equal(checks + 2, harness.Ops.TrustChecks);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.RootReplaced);
    }

    [Fact]
    public async Task AWatcherOverflowCausesAFullReconciliation()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.ps1", "b.ps1", "c.ps1");
        int scans = harness.Ops.Scans;
        int checks = harness.Ops.TrustChecks;

        // Repeated overflows while one reconciliation is pending add one reconciliation and one diagnostic.
        var gate = new Gate();
        harness.Ops.DiscoverGate = gate;
        harness.Scheduler.RequestRescan();
        await gate.Entered;
        for (int i = 0; i < 100; i++)
        {
            harness.Ops.Watchers[^1].Raise(WatcherChangeKind.Overflow);
        }

        harness.Ops.DiscoverGate = null;
        gate.Release();
        await harness.SettleAsync();
        Assert.Equal(scans + 2, harness.Ops.Scans);
        Assert.Equal(checks + 3, harness.Ops.TrustChecks);
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.WatcherOverflow);
    }

    [Fact]
    public async Task AFailedWatcherIsRecreated()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.ps1");
        FakeWatcher failed = harness.Ops.Watchers[^1];

        failed.Raise(WatcherChangeKind.Failed);
        await harness.SettleAsync();
        Assert.True(failed.Disposed);
        Assert.Equal(2, harness.Ops.Watchers.Count);

        // Notifications of the old watcher are ignored.
        int scans = harness.Ops.Scans;
        failed.Raise(WatcherChangeKind.Created, "new.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(scans, harness.Ops.Scans);
    }

    [Fact]
    public async Task WithoutAWatcherPeriodicReconciliationStillFindsChanges()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.WatcherProblem = "IOException";
        await harness.StartAsync("a.ps1");
        Assert.False(harness.Snapshot.Discovery.WatcherActive);
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.WatcherUnavailable);

        // A missed change (no notification) is found by the next periodic reconciliation, 30 s later.
        harness.Ops.Add("b.5s.ps1");
        harness.Ops.Approve("b.5s.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(29));
        Assert.Null(harness.Snapshot.Find(PluginId.Create("b.5s.ps1")));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, harness.Ops.Launches("b.5s.ps1"));
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.WatcherUnavailable);
    }

    [Fact]
    public async Task NotificationsAreDebouncedAndAStormCannotPostponeReconciliationIndefinitely()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.ps1");
        FakeWatcher watcher = harness.Ops.Watchers[^1];
        int scans = harness.Ops.Scans;

        // One notification: one rescan after 250 ms of quiet.
        watcher.Raise(WatcherChangeKind.Created, "b.ps1");
        await harness.AdvanceAsync(TimeSpan.FromMilliseconds(249));
        Assert.Equal(scans, harness.Ops.Scans);
        await harness.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        Assert.Equal(scans + 1, harness.Ops.Scans);

        // A continuous storm (a notification every 100 ms for 10 s): a rescan at least every second, never more than
        // one at a time, and never one per notification.
        scans = harness.Ops.Scans;
        for (int i = 0; i < 100; i++)
        {
            for (int j = 0; j < 50; j++)
            {
                watcher.Raise(WatcherChangeKind.Created, $"file{j}.tmp");
            }

            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        }

        Assert.InRange(harness.Ops.Scans - scans, 9, 11);
    }

    [Fact]
    public async Task ManyChangedPluginsAreBoundedByAFullTrustCheck()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.ps1");
        FakeWatcher watcher = harness.Ops.Watchers[^1];
        int checks = harness.Ops.TrustChecks;

        for (int i = 0; i < 10_000; i++)
        {
            watcher.Raise(WatcherChangeKind.Changed, $"dir/plugin{i}.ps1");
        }

        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(checks + 1, harness.Ops.TrustChecks);
    }

    [Theory]
    [InlineData("Changed", "data.json")]
    [InlineData("Changed", "sub/cache.txt")]
    [InlineData("Changed", "sub")]
    [InlineData("Created", ".git/index.lock")]
    [InlineData("Deleted", ".cache/a.ps1")]
    [InlineData("Changed", ".hidden/clock.5s.ps1")]
    public async Task AuxiliaryFileChangesCauseNoRescanAndNoCancellation(string kind, string name)
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        int scans = harness.Ops.Scans;

        harness.Ops.Watchers[^1].Raise(Enum.Parse<WatcherChangeKind>(kind), name);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(scans, harness.Ops.Scans);
        Assert.False(harness.Ops.LastRun("a.5s.ps1").CancellationRequested);
    }

    [Theory]
    [InlineData("Created", "b.ps1", null)]
    [InlineData("Changed", "a.5s.ps1", null)]
    [InlineData("Deleted", "a.5s.ps1", null)]
    [InlineData("Created", "newdir", null)]
    [InlineData("Deleted", "v1.2", null)]
    [InlineData("Renamed", "work", "old")]
    [InlineData("Renamed", "visible", ".hidden")]
    [InlineData("Renamed", "data.json", "data.json.tmp")]
    public void RelevantNotificationsRequestARescan(string kind, string name, string? oldName)
    {
        WatcherRelevance relevance = PluginRootWatcher.Classify(new WatcherNotification(Enum.Parse<WatcherChangeKind>(kind), name, oldName));
        Assert.True(relevance.Rescan);
    }

    [Fact]
    public void PluginFileNotificationsYieldOnlyIdentityHints()
    {
        WatcherRelevance changed = PluginRootWatcher.Classify(new WatcherNotification(WatcherChangeKind.Renamed, @"sub\b.ps1", @"sub\a.ps1"));
        Assert.Equal(PluginId.Create("sub/b.ps1"), changed.Plugin);
        Assert.Equal(PluginId.Create("sub/a.ps1"), changed.OldPlugin);
        Assert.Null(PluginRootWatcher.Classify(new WatcherNotification(WatcherChangeKind.Created, "dir", null)).Plugin);
        Assert.True(PluginRootWatcher.Classify(new WatcherNotification(WatcherChangeKind.Changed, null, null)).Rescan);
    }

    [Fact]
    public async Task RepeatedStartsCreateNoDuplicateWorkOrWatchers()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.Scheduler.StartAsync(SchedulerHarness.Token);
        await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => harness.Scheduler.StartAsync(SchedulerHarness.Token)));
        await harness.SettleAsync();

        Assert.Single(harness.Ops.Watchers);
        Assert.Equal(1, harness.Ops.Scans);
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));
        Assert.Equal(1, harness.Time.ArmedTimers);
    }

    [Fact]
    public async Task AScanAppliedLateDoesNotUndoNewerHostDecisions()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("a.5s.ps1");
        await harness.CompleteAsync("a.5s.ps1");

        // A scan blocks after listing; meanwhile the host disables the plugin and reports a trust change.
        var gate = new Gate();
        harness.Ops.DiscoverGate = gate;
        harness.Scheduler.RequestRescan();
        await gate.Entered;
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.SetEnabled(PluginId.Create("a.5s.ps1"), enabled: false));
        harness.Ops.Revoke("a.5s.ps1");
        harness.Scheduler.NotifyTrustChanged(PluginId.Create("a.5s.ps1"));
        harness.Scheduler.RequestRescan();
        gate.Release();
        harness.Ops.DiscoverGate = null;
        await harness.SettleAsync();

        ScheduledPlugin a = harness.Plugin("a.5s.ps1");
        Assert.False(a.IsEnabled);
        Assert.Equal(PluginTrustStatus.PendingApproval, a.ObservedTrustStatus);
        Assert.Null(a.Output);
        Assert.Equal(3, harness.Ops.Scans);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));
    }

    [Fact]
    public async Task ATrustCheckThatStartedBeforeAnInvalidationIsDiscarded()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.Add("a.5s.ps1");
        PluginApprovalIdentity first = harness.Ops.Approve("a.5s.ps1");
        var gate = new Gate();
        harness.Ops.CheckGate = gate;
        Task start = harness.Scheduler.StartAsync(SchedulerHarness.Token);
        await gate.Entered;

        // The check is in progress (it will see the first approval); the host reapproves and notifies meanwhile.
        harness.Scheduler.NotifyTrustChanged(PluginId.Create("a.5s.ps1"));
        harness.Ops.CheckGate = null;
        gate.Release();
        await start;
        await harness.SettleAsync();

        // The second, newer check decided; the first could not make the plugin eligible on its own.
        Assert.Equal(PluginTrustStatus.Approved, harness.Plugin("a.5s.ps1").ObservedTrustStatus);
        Assert.Equal(2, harness.Ops.TrustChecks);
        Assert.Equal(first, harness.Ops.LastRun("a.5s.ps1").Approval);
    }

    [Fact]
    public async Task TrackingIsBoundedEvenAcrossIncompleteScansWithDifferentPlugins()
    {
        await using var harness = new SchedulerHarness(hooks: new SchedulerTestHooks { MaxTrackedPlugins = 3 });
        for (int i = 0; i < 5; i++)
        {
            harness.Ops.Add($"p{i}.ps1");
        }

        await harness.StartAsync();
        Assert.Equal(3, harness.Snapshot.Plugins.Length);
        Assert.Equal(2, harness.Snapshot.Discovery.UntrackedPlugins);
        Assert.Single(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.TrackingCapacityReached);

        // Incomplete scans that each list other plugins cannot grow the state: uncertain entries are kept, new ones not added.
        harness.Ops.ScanComplete = false;
        for (int round = 0; round < 5; round++)
        {
            for (int i = 0; i < 5; i++)
            {
                harness.Ops.Delete(round == 0 ? $"p{i}.ps1" : $"r{round - 1}x{i}.ps1");
                harness.Ops.Add($"r{round}x{i}.ps1");
            }

            await harness.RescanAsync();
            Assert.Equal(3, harness.Snapshot.Plugins.Length);
        }

        // A complete scan removes the stale entries and makes room.
        harness.Ops.ScanComplete = true;
        await harness.RescanAsync();
        Assert.Equal(3, harness.Snapshot.Plugins.Length);
        Assert.All(harness.Snapshot.Plugins, plugin => Assert.StartsWith("r4", plugin.Id.Value, StringComparison.Ordinal));
    }

    [Fact]
    public async Task StoppingDuringAScanCancelsIt()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.Add("a.5s.ps1");
        var gate = new Gate();
        harness.Ops.DiscoverGate = gate;
        Task start = harness.Scheduler.StartAsync(SchedulerHarness.Token);
        await gate.Entered;

        SchedulerStopResult result = await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.True(result.IsComplete);
        Assert.Equal(SchedulerLifecycle.Stopped, result.Lifecycle);
        await start;
        Assert.True(harness.Ops.Watchers[^1].Disposed);
        Assert.Empty(harness.Snapshot.Plugins);
    }
}
