using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// Interval, manual and failure scheduling with a manual clock and fake operations.
public sealed class SchedulerTimingTests
{
    // Timers have millisecond resolution.
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);
    private static readonly string[] FairPlugins = ["a.250ms.ps1", "b.250ms.ps1", "c.250ms.ps1", "m.ps1"];

    [Fact]
    public async Task AnApprovedIntervalPluginRunsImmediatelyAfterDiscovery()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("clock.5s.ps1");

        Assert.Equal(1, harness.Ops.Launches("clock.5s.ps1"));
        Assert.Equal(TimeSpan.Zero, harness.Time.Elapsed);
        ScheduledPlugin plugin = harness.Plugin("clock.5s.ps1");
        Assert.Equal(PluginActivity.Running, plugin.Activity);
        Assert.Equal(PluginScheduleStatus.Running, plugin.Status);
        Assert.Equal(harness.Ops.ApprovalOf("clock.5s.ps1"), harness.Ops.LastRun("clock.5s.ps1").Approval);
    }

    [Fact]
    public async Task AManualOnlyPluginNeverRunsAutomatically()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("manual.ps1");
        await harness.AdvanceAsync(TimeSpan.FromDays(40));

        Assert.Equal(0, harness.Ops.Launches("manual.ps1"));
        Assert.Null(harness.Plugin("manual.ps1").NextRefreshUtc);
        Assert.Equal(PluginScheduleStatus.Idle, harness.Plugin("manual.ps1").Status);

        Assert.Equal(RefreshRequestStatus.Accepted, harness.Refresh("manual.ps1"));
        await harness.SettleAsync();
        Assert.Equal(1, harness.Ops.Launches("manual.ps1"));
        await harness.CompleteAsync("manual.ps1");
        Assert.Equal(RefreshTrigger.Manual, harness.Plugin("manual.ps1").LastRun!.Trigger);

        // Neither success nor failure gives a manual-only plugin automatic runs or retries.
        await harness.AdvanceAsync(TimeSpan.FromDays(1));
        Assert.Equal(RefreshRequestStatus.Accepted, harness.Refresh("manual.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        await harness.CompleteAsync("manual.ps1", RunOutcome.Failed);
        await harness.AdvanceAsync(TimeSpan.FromDays(1));
        Assert.Equal(2, harness.Ops.Launches("manual.ps1"));
        Assert.Null(harness.Plugin("manual.ps1").NextRefreshUtc);
    }

    [Fact]
    public async Task TheNextRunIsMeasuredFromTheEndOfThePreviousOne()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("clock.5s.ps1");

        // The run takes 2 s; the next is due 5 s after it ended, not 5 s after it started.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(2));
        await harness.CompleteAsync("clock.5s.ps1");
        Assert.Equal(ManualTimeProvider.Epoch + TimeSpan.FromSeconds(7), harness.Plugin("clock.5s.ps1").NextRefreshUtc);

        await harness.AdvanceAsync(TimeSpan.FromSeconds(5) - Tick);
        Assert.Equal(1, harness.Ops.Launches("clock.5s.ps1"));
        await harness.AdvanceAsync(Tick);
        Assert.Equal(2, harness.Ops.Launches("clock.5s.ps1"));
        Assert.Equal(PluginRunDisposition.Published, harness.Plugin("clock.5s.ps1").LastRun!.Disposition);
    }

    [Theory]
    [InlineData("fast.250ms.ps1", 250L * TimeSpan.TicksPerMillisecond)]
    [InlineData("slow.30d.ps1", 30L * TimeSpan.TicksPerDay)]
    public async Task TheShortestAndLongestIntervalsAreHonoredExactly(string id, long intervalTicks)
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync(id);
        await harness.CompleteAsync(id);

        await harness.AdvanceAsync(TimeSpan.FromTicks(intervalTicks) - Tick);
        Assert.Equal(1, harness.Ops.Launches(id));
        await harness.AdvanceAsync(Tick);
        Assert.Equal(2, harness.Ops.Launches(id));
    }

    [Fact]
    public async Task MissedIntervalsAreNotCaughtUp()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("tick.1s.ps1");

        // A 1 s plugin whose run takes a minute (or a suspended machine): exactly one run follows, one interval later.
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1));
        await harness.CompleteAsync("tick.1s.ps1");
        Assert.Equal(1, harness.Ops.Launches("tick.1s.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, harness.Ops.Launches("tick.1s.ps1"));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, harness.Ops.Launches("tick.1s.ps1"));
    }

    [Fact]
    public async Task FailuresBackOffAndASuccessResetsTheSchedule()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("flaky.1s.ps1");

        int launches = 1;
        foreach (int seconds in new[] { 5, 10, 20, 40, 60, 60 })
        {
            await harness.CompleteAsync("flaky.1s.ps1", RunOutcome.Failed);
            Assert.Equal(PluginScheduleStatus.Failed, harness.Plugin("flaky.1s.ps1").Status);
            await harness.AdvanceAsync(TimeSpan.FromSeconds(seconds) - Tick);
            Assert.Equal(launches, harness.Ops.Launches("flaky.1s.ps1"));
            await harness.AdvanceAsync(Tick);
            Assert.Equal(++launches, harness.Ops.Launches("flaky.1s.ps1"));
        }

        Assert.Equal(6, harness.Plugin("flaky.1s.ps1").ConsecutiveFailures);
        await harness.CompleteAsync("flaky.1s.ps1");
        Assert.Equal(0, harness.Plugin("flaky.1s.ps1").ConsecutiveFailures);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(++launches, harness.Ops.Launches("flaky.1s.ps1"));
    }

    [Fact]
    public async Task ALongerIntervalIsNeverShortenedByTheFailureDelay()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("slow.2m.ps1");
        await harness.CompleteAsync("slow.2m.ps1", RunOutcome.Failed);

        await harness.AdvanceAsync(TimeSpan.FromMinutes(2) - Tick);
        Assert.Equal(1, harness.Ops.Launches("slow.2m.ps1"));
        await harness.AdvanceAsync(Tick);
        Assert.Equal(2, harness.Ops.Launches("slow.2m.ps1"));
    }

    [Fact]
    public async Task ManualRefreshDuringARunIsCoalescedIntoOneFollowUp()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("clock.1m.ps1");

        Assert.Equal(RefreshRequestStatus.Accepted, harness.Refresh("clock.1m.ps1"));
        Assert.Equal(RefreshRequestStatus.Coalesced, harness.Refresh("clock.1m.ps1"));
        Assert.Equal(RefreshRequestStatus.Coalesced, harness.Refresh("clock.1m.ps1"));
        Assert.True(harness.Plugin("clock.1m.ps1").ManualRefreshPending);
        await harness.SettleAsync();
        Assert.Equal(1, harness.Ops.Launches("clock.1m.ps1"));
        Assert.Equal(1, harness.Ops.MaxConcurrentPerKey);

        // The follow-up waits for the end of the run plus the minimum relaunch delay.
        await harness.CompleteAsync("clock.1m.ps1");
        Assert.Equal(1, harness.Ops.Launches("clock.1m.ps1"));
        Assert.Equal(PluginActivity.Queued, harness.Plugin("clock.1m.ps1").Activity);
        await harness.AdvanceAsync(PluginSchedulingPolicy.MinimumRelaunchDelay - Tick);
        Assert.Equal(1, harness.Ops.Launches("clock.1m.ps1"));
        await harness.AdvanceAsync(Tick);
        Assert.Equal(2, harness.Ops.Launches("clock.1m.ps1"));
        Assert.False(harness.Plugin("clock.1m.ps1").ManualRefreshPending);

        // The manual run resets the periodic schedule: the next automatic run is a full interval after it.
        TimeSpan manualEnded = harness.Time.Elapsed + TimeSpan.FromSeconds(3);
        await harness.AdvanceAsync(TimeSpan.FromSeconds(3));
        await harness.CompleteAsync("clock.1m.ps1");
        Assert.Equal(RefreshTrigger.Manual, harness.Plugin("clock.1m.ps1").LastRun!.Trigger);
        Assert.Equal(ManualTimeProvider.Epoch + manualEnded + TimeSpan.FromMinutes(1), harness.Plugin("clock.1m.ps1").NextRefreshUtc);
        await harness.AdvanceAsync(TimeSpan.FromMinutes(1) - Tick);
        Assert.Equal(2, harness.Ops.Launches("clock.1m.ps1"));
        await harness.AdvanceAsync(Tick);
        Assert.Equal(3, harness.Ops.Launches("clock.1m.ps1"));
    }

    [Fact]
    public async Task ManualRefreshesNeverBypassTheMinimumRelaunchDelay()
    {
        await using var harness = new SchedulerHarness();
        harness.Ops.AutoComplete = _ => RunOutcome.Failed;
        await harness.StartAsync("manual.ps1");

        // A host spamming refreshes of a plugin that fails at once gets at most one run per 250 ms.
        for (int i = 0; i < 40; i++)
        {
            harness.Refresh("manual.ps1");
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(25));
        }

        Assert.InRange(harness.Ops.Launches("manual.ps1"), 3, 5);
    }

    [Fact]
    public async Task TheGlobalConcurrencyLimitHolds()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 2 });
        await harness.StartAsync("a.1s.ps1", "b.1s.ps1", "c.1s.ps1", "d.1s.ps1", "e.1s.ps1");

        Assert.Equal(2, harness.Ops.ActiveRuns.Count);
        Assert.Equal(2, harness.Snapshot.ActiveRuns);
        Assert.Equal(0, harness.Snapshot.AvailableSlots);
        Assert.Equal(3, harness.Snapshot.Plugins.Count(plugin => plugin.Activity == PluginActivity.Queued && plugin.Status == PluginScheduleStatus.Queued));

        // Each completion frees one slot for the plugin that has waited longest.
        for (int round = 0; round < 20; round++)
        {
            harness.Ops.ActiveRuns[0].Complete(RunOutcome.Succeeded);
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(300));
            Assert.Equal(2, harness.Ops.ActiveRuns.Count);
        }

        Assert.Equal(2, harness.Ops.MaxConcurrentRuns);
        Assert.Equal(1, harness.Ops.MaxConcurrentPerKey);
    }

    [Fact]
    public async Task APluginNeverHasTwoRunsAtOnce()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("clock.250ms.ps1");

        // Far past its due time, with manual requests: still one run.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(10));
        harness.Refresh("clock.250ms.ps1");
        await harness.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, harness.Ops.Launches("clock.250ms.ps1"));
        Assert.Equal(1, harness.Ops.MaxConcurrentPerKey);
    }

    [Fact]
    public async Task ReadyPluginsAreServedFairly()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { MaxConcurrency = 1 });
        harness.Ops.AutoComplete = _ => RunOutcome.Succeeded;
        await harness.StartAsync("a.250ms.ps1", "b.250ms.ps1", "c.250ms.ps1", "m.ps1");

        // Every plugin is always ready and "a" is also refreshed manually all the time; with one slot, every plugin still
        // gets its turn in every round.
        for (int i = 0; i < 60; i++)
        {
            harness.Refresh("a.250ms.ps1");
            harness.Refresh("m.ps1");
            await harness.AdvanceAsync(TimeSpan.FromMilliseconds(100));
        }

        int[] counts = [.. FairPlugins.Select(harness.Ops.Launches)];
        Assert.True(counts.Min() >= 15, string.Join(", ", counts));
        Assert.True(counts.Max() - counts.Min() <= 1, string.Join(", ", counts));
        Assert.Equal(1, harness.Ops.MaxConcurrentRuns);
    }

    [Fact]
    public async Task SchedulingUsesTheMonotonicClockNotTheWallClock()
    {
        await using var harness = new SchedulerHarness();
        await harness.StartAsync("clock.10s.ps1");
        await harness.CompleteAsync("clock.10s.ps1");

        harness.Time.JumpWallClock(TimeSpan.FromDays(-1));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        harness.Time.JumpWallClock(TimeSpan.FromDays(365));
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5) - Tick);
        Assert.Equal(1, harness.Ops.Launches("clock.10s.ps1"));
        await harness.AdvanceAsync(Tick);
        Assert.Equal(2, harness.Ops.Launches("clock.10s.ps1"));
    }

    [Fact]
    public async Task ARefreshOfAnUnknownPluginOrAStoppedSchedulerIsRejected()
    {
        await using var harness = new SchedulerHarness();
        Assert.Equal(RefreshRequestStatus.NotRunning, harness.Refresh("a.ps1"));
        await harness.StartAsync("a.ps1");
        Assert.Equal(RefreshRequestStatus.UnknownPlugin, harness.Refresh("b.ps1"));
        Assert.Equal(RefreshRequestStatus.Accepted, harness.Scheduler.RequestRefresh(PluginId.Create("A.PS1")));
        await harness.Scheduler.StopAsync(SchedulerHarness.Token);
        Assert.Equal(RefreshRequestStatus.NotRunning, harness.Refresh("a.ps1"));
    }
}
