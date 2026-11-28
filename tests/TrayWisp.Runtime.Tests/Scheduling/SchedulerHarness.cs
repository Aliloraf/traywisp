using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Scheduling;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// A scheduler on fake operations and a manual clock. Background work still runs on the thread pool; SettleAsync waits
// until nothing but the (test-controlled) runs is in progress, and AdvanceAsync moves the clock one timer at a time,
// settling after each, so time-based behavior is deterministic.
internal sealed class SchedulerHarness : IAsyncDisposable
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    public SchedulerHarness(PluginSchedulerOptions? options = null, SchedulerTestHooks? hooks = null)
    {
        Root = PluginRoot.Create(@"C:\plugins");
        Time = new ManualTimeProvider();
        Ops = new FakeSchedulerOperations(Root);
        Scheduler = new PluginScheduler(Ops, Root, options ?? new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.FromSeconds(5) }, Time, hooks);
    }

    public PluginRoot Root { get; }

    public ManualTimeProvider Time { get; }

    public FakeSchedulerOperations Ops { get; }

    public PluginScheduler Scheduler { get; }

    public PluginSchedulerSnapshot Snapshot => Scheduler.Snapshot;

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    // Adds and approves plugins, then starts the scheduler and settles.
    public async Task StartAsync(params string[] approved)
    {
        foreach (string id in approved)
        {
            Ops.Add(id);
            Ops.Approve(id);
        }

        await Scheduler.StartAsync(Token).WaitAsync(Guard, Token);
        await SettleAsync();
    }

    public async Task SettleAsync() => await Scheduler.WhenIdleAsync().WaitAsync(Guard, Token);

    // Moves the clock forward by duration, firing every timer that becomes due on the way, in order.
    public async Task AdvanceAsync(TimeSpan duration)
    {
        long target = Time.GetTimestamp() + duration.Ticks;
        while (true)
        {
            await SettleAsync();
            if (Time.NextDue() is not long next || next > target)
            {
                break;
            }

            Time.FireNext();
        }

        Time.SetNow(target);
        await SettleAsync();
    }

    public ScheduledPlugin Plugin(string id) => Snapshot.Find(PluginId.Create(id)) ?? throw new Xunit.Sdk.XunitException($"{id} is not tracked.");

    public RefreshRequestStatus Refresh(string id) => Scheduler.RequestRefresh(PluginId.Create(id));

    public async Task RescanAsync()
    {
        Assert.True(Scheduler.RequestRescan());
        await SettleAsync();
    }

    // Completes the plugin's active run and settles.
    public async Task CompleteAsync(string id, RunOutcome outcome = RunOutcome.Succeeded)
    {
        FakeRun run = Assert.Single(Ops.ActiveRuns, run => run.Id == PluginId.Create(id));
        run.Complete(outcome);
        await SettleAsync();
    }

    // Waits (bounded) until the snapshot satisfies the condition.
    public async Task<PluginSchedulerSnapshot> WaitForAsync(Func<PluginSchedulerSnapshot, bool> condition)
    {
        PluginSchedulerSnapshot snapshot = Snapshot;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Guard);
        while (!condition(snapshot))
        {
            snapshot = await Scheduler.WaitForChangeAsync(snapshot.Revision, timeout.Token);
        }

        return snapshot;
    }

    public async ValueTask DisposeAsync()
    {
        // Lets blocked operations and runs that ignore cancellation end, so no background work outlives the test.
        Ops.DiscoverGate?.Release();
        Ops.CheckGate?.Release();
        Ops.CheckResultGate?.Release();
        Ops.ApprovalCheckGate?.Release();
        foreach (FakeRun run in Ops.ActiveRuns)
        {
            run.Complete(RunOutcome.Failed);
        }

        await Scheduler.StopAsync(Token);
    }
}
