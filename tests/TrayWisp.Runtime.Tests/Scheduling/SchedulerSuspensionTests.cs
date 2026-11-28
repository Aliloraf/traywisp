using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// Suspension while the state of the root or a plugin is unresolved: root loss, root replacement and incomplete discovery.
// Trust checks are blocked (CheckGate) so that the snapshots before any check has been applied can be inspected.
public sealed class SchedulerSuspensionTests
{
    private static PluginId Id(string value) => PluginId.Create(value);

    private static SchedulerDiagnostic RootGone { get; } = new(PluginDiagnosticSeverity.Warning, SchedulerDiagnosticCode.RootUnavailable, "gone");

    // Starts with "a.5s.ps1" running and the manual plugin "b.ps1" showing published output.
    private static async Task<(FakeRun Running, PublishedPluginOutput Output)> StartWithOutputAsync(SchedulerHarness harness)
    {
        await harness.StartAsync("a.5s.ps1", "b.ps1");
        Assert.Equal(RefreshRequestStatus.Accepted, harness.Refresh("b.ps1"));
        await harness.SettleAsync();
        await harness.CompleteAsync("b.ps1");
        return (harness.Ops.LastRun("a.5s.ps1"), harness.Plugin("b.ps1").Output!);
    }

    [Fact]
    public async Task ALostRootSuspendsEveryPluginAtOnceWithoutTrustChecks()
    {
        await using var harness = new SchedulerHarness();
        (FakeRun running, _) = await StartWithOutputAsync(harness);
        PluginApprovalIdentity approval = harness.Ops.ApprovalOf("b.ps1")!;
        long revision = harness.Plugin("b.ps1").LifecycleRevision;
        var checks = new Gate();
        harness.Ops.CheckGate = checks;
        int trustChecks = harness.Ops.TrustChecks;

        harness.Ops.RootProblem = RootGone;
        Assert.True(harness.Scheduler.RequestRescan());
        PluginSchedulerSnapshot lost = await harness.WaitForAsync(snapshot => !snapshot.Discovery.RootAvailable);

        // In the snapshot that reports the loss, before any trust check: output withheld, run cancelled, nothing launchable.
        Assert.True(running.CancellationRequested);
        Assert.All(lost.Plugins, plugin =>
        {
            Assert.Equal(PluginPresence.Uncertain, plugin.Presence);
            Assert.Equal(PluginEligibility.Uncertain, plugin.Eligibility);
            Assert.Null(plugin.Output);
            Assert.True(plugin.TrustCheckPending);
        });
        Assert.True(lost.Find(Id("b.ps1"))!.LifecycleRevision > revision);
        Assert.Equal(RefreshRequestStatus.Uncertain, harness.Refresh("b.ps1"));

        // Identities and approvals are kept, and no trust check is made while the root is unavailable, also on retries.
        await harness.SettleAsync();
        await harness.AdvanceAsync(TimeSpan.FromSeconds(20));
        Assert.False(checks.Entered.IsCompleted);
        Assert.Equal(trustChecks, harness.Ops.TrustChecks);
        Assert.Equal(approval, harness.Ops.ApprovalOf("b.ps1"));
        Assert.Equal(2, harness.Snapshot.Plugins.Length);
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));
        Assert.Equal(0, harness.Snapshot.ActiveRuns);
        Assert.Equal(PluginRunDisposition.Superseded, harness.Plugin("a.5s.ps1").LastRun!.Disposition);
    }

    [Fact]
    public async Task ALostRootAlsoWithdrawsTheOutputOfAPluginThatWasAlreadyUncertain()
    {
        await using var harness = new SchedulerHarness();
        await StartWithOutputAsync(harness);

        // "b" is missing from an incomplete scan, and its targeted check confirms its approval: its output is shown again.
        var discover = new Gate();
        harness.Ops.DiscoverGate = discover;
        harness.Ops.ScanComplete = false;
        harness.Ops.Delete("b.ps1");
        harness.Scheduler.RequestRescan();
        await discover.Entered;
        harness.Ops.Add("b.ps1");
        harness.Ops.DiscoverGate = null;
        discover.Release();
        await harness.SettleAsync();
        Assert.Equal(PluginPresence.Uncertain, harness.Plugin("b.ps1").Presence);
        Assert.NotNull(harness.Plugin("b.ps1").Output);

        // The root is then lost: the already uncertain plugin is suspended as well.
        harness.Ops.RootProblem = RootGone;
        await harness.RescanAsync();
        Assert.Null(harness.Plugin("b.ps1").Output);
        Assert.True(harness.Plugin("b.ps1").TrustCheckPending);
    }

    [Fact]
    public async Task AnIncompleteScanWithholdsOutputUntilItsTargetedCheckConfirmsIt()
    {
        await using var harness = new SchedulerHarness();
        (FakeRun running, PublishedPluginOutput output) = await StartWithOutputAsync(harness);
        var checks = new Gate();
        harness.Ops.CheckGate = checks;

        // "b" is missing from the listing of an incomplete scan, but its file is there for the trust check.
        var discover = new Gate();
        harness.Ops.DiscoverGate = discover;
        harness.Ops.ScanComplete = false;
        harness.Ops.Delete("b.ps1");
        harness.Scheduler.RequestRescan();
        await discover.Entered;
        harness.Ops.Add("b.ps1");
        harness.Ops.DiscoverGate = null;
        discover.Release();
        await checks.Entered;

        // Before the check: withheld, not removed, approval kept; the incomplete scan is no reason to cancel another run.
        ScheduledPlugin pending = harness.Plugin("b.ps1");
        Assert.Equal(PluginPresence.Uncertain, pending.Presence);
        Assert.Null(pending.Output);
        Assert.True(pending.TrustCheckPending);
        Assert.NotNull(harness.Ops.ApprovalOf("b.ps1"));
        Assert.False(running.CancellationRequested);

        // The check confirms the approval the output was produced under: the same output is published again.
        harness.Ops.CheckGate = null;
        checks.Release();
        await harness.SettleAsync();
        ScheduledPlugin confirmed = harness.Plugin("b.ps1");
        Assert.Same(output, confirmed.Output);
        Assert.Equal(PluginPresence.Uncertain, confirmed.Presence);
        Assert.Equal(RefreshRequestStatus.Uncertain, harness.Refresh("b.ps1"));
    }

    [Fact]
    public async Task ARecoveredRootNeedsAFreshCheckBeforeOutputOrLaunches()
    {
        await using var harness = new SchedulerHarness();
        (FakeRun running, PublishedPluginOutput output) = await StartWithOutputAsync(harness);
        harness.Ops.RootProblem = RootGone;
        await harness.RescanAsync();
        Assert.True(running.CancellationRequested);

        var checks = new Gate();
        harness.Ops.CheckGate = checks;
        harness.Ops.RootProblem = null;
        harness.Scheduler.RequestRescan();
        await checks.Entered;

        // Listed again, but nothing is published or launched before the fresh check.
        PluginSchedulerSnapshot listed = harness.Snapshot;
        Assert.True(listed.Discovery.RootAvailable);
        Assert.All(listed.Plugins, plugin =>
        {
            Assert.Equal(PluginPresence.Present, plugin.Presence);
            Assert.Equal(PluginEligibility.AwaitingTrustCheck, plugin.Eligibility);
            Assert.Null(plugin.Output);
        });
        Assert.Equal(1, harness.Ops.Launches("a.5s.ps1"));

        harness.Ops.CheckGate = null;
        checks.Release();
        await harness.SettleAsync();
        Assert.Same(output, harness.Plugin("b.ps1").Output);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, harness.Ops.Launches("a.5s.ps1"));
    }

    [Fact]
    public async Task AReplacedRootSuspendsEveryPluginUntilTheFreshChecksOfTheSamePass()
    {
        await using var harness = new SchedulerHarness();
        (FakeRun running, PublishedPluginOutput output) = await StartWithOutputAsync(harness);
        int trustChecks = harness.Ops.TrustChecks;
        var checks = new Gate();
        harness.Ops.CheckGate = checks;

        harness.Ops.Identity = new RootIdentity(1, 2, 3);
        harness.Scheduler.RequestRescan();
        await checks.Entered;

        // During the first fresh check of the pass that noticed the replacement: everything is suspended.
        Assert.True(running.CancellationRequested);
        Assert.Contains(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.RootReplaced);
        Assert.All(harness.Snapshot.Plugins, plugin =>
        {
            Assert.Equal(PluginEligibility.AwaitingTrustCheck, plugin.Eligibility);
            Assert.Null(plugin.Output);
        });
        Assert.NotNull(harness.Ops.ApprovalOf("a.5s.ps1"));
        Assert.NotNull(harness.Ops.ApprovalOf("b.ps1"));

        // Replacement alone revokes nothing: the trust service approves the unchanged files at the root.
        harness.Ops.CheckGate = null;
        checks.Release();
        await harness.SettleAsync();
        Assert.Equal(trustChecks + 2, harness.Ops.TrustChecks);
        Assert.Same(output, harness.Plugin("b.ps1").Output);
        Assert.Equal(PluginEligibility.Eligible, harness.Plugin("a.5s.ps1").Eligibility);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, harness.Ops.Launches("a.5s.ps1"));
    }

    [Fact]
    public async Task AReplacedRootWhosePluginChangedLosesItsApprovalThroughTheTrustService()
    {
        await using var harness = new SchedulerHarness();
        await StartWithOutputAsync(harness);

        harness.Ops.Identity = new RootIdentity(1, 2, 3);
        harness.Ops.ModifyContentOnly("b.ps1");
        await harness.RescanAsync();

        Assert.Equal(PluginTrustStatus.ContentChanged, harness.Plugin("b.ps1").ObservedTrustStatus);
        Assert.Null(harness.Plugin("b.ps1").Output);
        Assert.Null(harness.Ops.ApprovalOf("b.ps1"));
        Assert.Equal(PluginTrustStatus.Approved, harness.Plugin("a.5s.ps1").ObservedTrustStatus);
    }
}
