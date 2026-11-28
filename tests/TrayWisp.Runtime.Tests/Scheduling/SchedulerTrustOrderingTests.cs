using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// The ordering of trust observations. A reconciliation check has already obtained an approval, but its application is held
// back (CheckResultGate blocks after the decision, not before the check); a newer, incompatible decision is applied meanwhile.
// The older approval must not restore eligibility. Execution authorization is never at stake here: every launch makes its
// own check. What these tests guard is the scheduler's state, which must not ask the engine to launch on obsolete grounds.
public sealed class SchedulerTrustOrderingTests
{
    private const string Plugin = "a.5s.ps1";

    private static PluginId Id => PluginId.Create(Plugin);

    // Starts with the plugin running, then holds back the approved result of a full reconciliation's check of it.
    private static async Task<Gate> HoldApprovedCheckAsync(SchedulerHarness harness)
    {
        var held = new Gate();
        harness.Ops.CheckResultGate = held;
        Assert.True(harness.Scheduler.RequestReconciliation());
        await held.Entered;
        harness.Ops.CheckResultGate = null;
        return held;
    }

    [Fact]
    public async Task ADelayedApprovalCannotUndoANewerContentChangeFromTheOutputCheck()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync(Plugin);
        FakeRun run = harness.Ops.LastRun(Plugin);
        Gate held = await HoldApprovedCheckAsync(harness);
        int checks = harness.Ops.TrustChecks;

        // Newer: the content changes and the run ends; its output check observes the change, and the trust service
        // invalidates the approval persistently.
        harness.Ops.ModifyContentOnly(Plugin);
        run.Succeed();
        ScheduledPlugin denied = (await harness.WaitForAsync(snapshot => snapshot.Find(Id)!.LastRun is not null)).Find(Id)!;
        Assert.Equal(PluginTrustStatus.ContentChanged, denied.ObservedTrustStatus);
        Assert.Equal(PluginEligibility.PendingApproval, denied.Eligibility);
        Assert.Null(denied.Output);
        Assert.Null(harness.Ops.ApprovalOf(Plugin));

        // Older: the held approval is applied late. It is stale: discarded, and one fresh check confirms the denial.
        held.Release();
        await harness.SettleAsync();
        ScheduledPlugin after = harness.Plugin(Plugin);
        Assert.Equal(PluginTrustStatus.PendingApproval, after.ObservedTrustStatus);
        Assert.Equal(PluginEligibility.PendingApproval, after.Eligibility);
        Assert.Null(after.Output);
        Assert.Equal(checks + 1, harness.Ops.TrustChecks);

        // Nothing was ever asked to launch on the obsolete approval.
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(0, harness.Ops.DeniedRuns);
        Assert.Equal(1, harness.Ops.EngineCalls);
        Assert.Equal(PluginEligibility.PendingApproval, harness.Plugin(Plugin).Eligibility);
    }

    [Fact]
    public async Task ADelayedApprovalCannotUndoANewerUnavailableStoreAndALaterApprovalStillCounts()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync(Plugin);
        FakeRun run = harness.Ops.LastRun(Plugin);
        Gate held = await HoldApprovedCheckAsync(harness);

        // Newer: the store becomes unavailable and the run's output check observes it.
        harness.Ops.StoreUnavailable = true;
        run.Succeed();
        ScheduledPlugin denied = (await harness.WaitForAsync(snapshot => snapshot.Find(Id)!.LastRun is not null)).Find(Id)!;
        Assert.Equal(PluginTrustStatus.StoreUnavailable, denied.ObservedTrustStatus);
        Assert.Equal(PluginEligibility.Unavailable, denied.Eligibility);

        held.Release();
        await harness.SettleAsync();
        Assert.Equal(PluginEligibility.Unavailable, harness.Plugin(Plugin).Eligibility);
        Assert.Equal(PluginTrustStatus.StoreUnavailable, harness.Plugin(Plugin).ObservedTrustStatus);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, harness.Ops.DeniedRuns);
        Assert.Equal(1, harness.Ops.EngineCalls);

        // The store is back: a fresh check (requested here explicitly) finds the unchanged approval, and the plugin runs.
        harness.Ops.StoreUnavailable = false;
        Assert.True(harness.Scheduler.RequestReconciliation());
        await harness.AdvanceAsync(PluginSchedulingPolicy.MinimumRelaunchDelay);
        Assert.Equal(PluginEligibility.Eligible, harness.Plugin(Plugin).Eligibility);
        Assert.Equal(2, harness.Ops.Launches(Plugin));
        Assert.Equal(0, harness.Ops.DeniedRuns);
    }

    [Fact]
    public async Task ADelayedApprovalCannotUndoANewerLaunchTimeDenial()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync(Plugin);
        await harness.CompleteAsync(Plugin);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Gate held = await HoldApprovedCheckAsync(harness);

        // Newer: the content changes and a manual refresh reaches the engine, whose launch-time check denies it.
        harness.Ops.ModifyContentOnly(Plugin);
        Assert.Equal(RefreshRequestStatus.Accepted, harness.Refresh(Plugin));
        ScheduledPlugin denied = (await harness.WaitForAsync(snapshot => snapshot.Find(Id)!.LastRun?.ExecutionStatus == PluginExecutionStatus.TrustDenied)).Find(Id)!;
        Assert.Equal(PluginTrustStatus.ContentChanged, denied.ObservedTrustStatus);
        Assert.Null(denied.Output);
        Assert.Equal(1, harness.Ops.DeniedRuns);

        held.Release();
        await harness.SettleAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        Assert.Equal(PluginEligibility.PendingApproval, harness.Plugin(Plugin).Eligibility);
        Assert.Equal(1, harness.Ops.DeniedRuns);
        Assert.Equal(1, harness.Ops.Launches(Plugin));
    }

    [Fact]
    public async Task ADelayedApprovalOfTheOldGenerationGivesWayToAReapproval()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync(Plugin);
        FakeRun first = harness.Ops.LastRun(Plugin);
        Gate held = await HoldApprovedCheckAsync(harness);

        // Newer: revoked and approved again by the host, which notifies the scheduler.
        harness.Ops.Revoke(Plugin);
        PluginApprovalIdentity second = harness.Ops.Approve(Plugin);
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged(Id));
        Assert.True(first.CancellationRequested);
        ScheduledPlugin waiting = harness.Plugin(Plugin);
        Assert.Equal(PluginEligibility.AwaitingTrustCheck, waiting.Eligibility);
        Assert.Null(waiting.Output);

        // The held approval of the first generation is discarded; the requested fresh check finds the second, which runs.
        held.Release();
        await harness.SettleAsync();
        await harness.AdvanceAsync(PluginSchedulingPolicy.MinimumRelaunchDelay);
        Assert.Equal(PluginEligibility.Eligible, harness.Plugin(Plugin).Eligibility);
        Assert.Equal(2, harness.Ops.Launches(Plugin));
        Assert.Equal(second, harness.Ops.LastRun(Plugin).Approval);
        Assert.Equal(0, harness.Ops.DeniedRuns);
        Assert.Equal(1, harness.Ops.MaxConcurrentPerKey);

        await harness.CompleteAsync(Plugin);
        Assert.Equal(second, harness.Plugin(Plugin).Output!.Approval);
    }

    [Fact]
    public async Task ADelayedApprovalGivesWayToARevocationTheHostReported()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync(Plugin);
        Gate held = await HoldApprovedCheckAsync(harness);

        harness.Ops.Revoke(Plugin);
        harness.Scheduler.NotifyTrustChanged(Id);
        held.Release();
        await harness.SettleAsync();
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));

        Assert.Equal(PluginEligibility.PendingApproval, harness.Plugin(Plugin).Eligibility);
        Assert.Equal(1, harness.Ops.EngineCalls);
        Assert.Equal(0, harness.Ops.DeniedRuns);
    }

    [Fact]
    public async Task AStaleDenialDecidesNothingWhileAFresherCheckIsPending()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.Add(Plugin);
        harness.Ops.Approve(Plugin);

        // The initial check observes an unavailable store; its denial is held back.
        harness.Ops.StoreUnavailable = true;
        var held = new Gate();
        harness.Ops.CheckResultGate = held;
        Task start = harness.Scheduler.StartAsync(SchedulerHarness.Token);
        await held.Entered;
        harness.Ops.CheckResultGate = null;
        harness.Ops.StoreUnavailable = false;

        // Newer: the store is back and the host notifies a trust change. Its check cannot start before the held one
        // ends (passes never overlap), so the epoch alone makes the held denial stale.
        harness.Scheduler.NotifyTrustChanged(Id);
        held.Release();
        await start;
        await harness.SettleAsync();

        // The stale denial decided nothing; the fresh check approved and the plugin runs.
        Assert.Equal(PluginTrustStatus.Approved, harness.Plugin(Plugin).ObservedTrustStatus);
        Assert.Equal(1, harness.Ops.Launches(Plugin));
        Assert.Equal(2, harness.Ops.TrustChecks);
    }
}
