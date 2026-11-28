using System.Diagnostics;
using System.Runtime.Versioning;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Scheduling;
using TrayWisp.Runtime.Tests.Execution;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// The real scheduler on Windows: temporary plugin roots, an isolated trust store, the real watcher, discovery, trust
// service and execution engine, with the controlled helper program as interpreter. Waits are event-driven (snapshot
// revisions) and bounded.
[SupportedOSPlatform("windows")]
public sealed class SchedulerIntegrationTests : IAsyncLifetime
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();
    private PluginScheduler? _scheduler;

    private static CancellationToken Token => ExecutionTestEnvironment.Token;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        if (_scheduler is not null)
        {
            await _scheduler.DisposeAsync();
        }

        _env.Dispose();
    }

    private PluginScheduler Create(TimeSpan? reconciliation = null)
    {
        PluginExecutionService execution = _env.Service();
        _scheduler = new PluginScheduler(execution, _env.Root, new PluginSchedulerOptions { ReconciliationInterval = reconciliation ?? TimeSpan.FromHours(1) });
        return _scheduler;
    }

    private static async Task<PluginSchedulerSnapshot> WaitForAsync(PluginScheduler scheduler, Func<PluginSchedulerSnapshot, bool> condition, string what)
    {
        PluginSchedulerSnapshot snapshot = scheduler.Snapshot;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Bound);
        try
        {
            while (!condition(snapshot))
            {
                snapshot = await scheduler.WaitForChangeAsync(snapshot.Revision, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!Token.IsCancellationRequested)
        {
            Assert.Fail($"Timed out waiting for {what}. Last snapshot: {Describe(snapshot)}");
        }

        return snapshot;
    }

    private static string Describe(PluginSchedulerSnapshot snapshot) =>
        string.Join("; ", snapshot.Plugins.Select(plugin => $"{plugin.Id} {plugin.Status} trust={plugin.ObservedTrustStatus} run={plugin.LastRun?.Disposition}/{plugin.LastRun?.ExecutionStatus}"))
        + " | " + string.Join(" ", snapshot.Diagnostics.Select(diagnostic => diagnostic.Code));

    private static PluginId Id(string value) => PluginId.Create(value);

    [Fact]
    public async Task DiscoveryApprovalExecutionModificationReapprovalAndShutdownWorkEndToEnd()
    {
        string marker = _env.MarkerPath("hello");
        _env.Write("hello.ps1", $"write-file {marker}|ran", "out Hello", "out ---", "out Item");
        PluginScheduler scheduler = Create();
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);

        // Discovered, pending, and genuinely not executed.
        ScheduledPlugin pending = scheduler.Snapshot.Find(Id("hello.ps1"))!;
        Assert.Equal(PluginScheduleStatus.PendingApproval, pending.Status);
        Assert.True(scheduler.Snapshot.Discovery.WatcherActive);
        Assert.Equal(RefreshRequestStatus.NotApproved, scheduler.RequestRefresh(Id("hello.ps1")));
        Assert.Equal(0, _env.ProcessStarts);
        Assert.False(File.Exists(marker));

        // Approved by the host with the shared trust service, then reported.
        PluginApproval first = _env.Trust.Approve(_env.TrustService, "hello.ps1");
        scheduler.NotifyTrustChanged(Id("hello.ps1"));
        await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("hello.ps1"))?.ObservedTrustStatus == PluginTrustStatus.Approved, "approval");
        Assert.Equal(RefreshRequestStatus.Accepted, scheduler.RequestRefresh(Id("hello.ps1")));
        PluginSchedulerSnapshot published = await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("hello.ps1"))?.Output is not null, "published output");
        PublishedPluginOutput output = published.Find(Id("hello.ps1"))!.Output!;
        Assert.Equal("Hello", Assert.Single(output.Output.Titles).Item.Text);
        Assert.Equal(first.Identity, output.Approval);
        Assert.Equal(PluginTrustStatus.Approved, _env.TrustService.CheckTrust(output.Approval, Token).Status);
        Assert.Equal(1, _env.ProcessStarts);
        Assert.True(File.Exists(marker));

        // Edited between runs: the watcher notices, the trust service confirms the change, the output is withdrawn.
        File.Delete(marker);
        _env.Write("hello.ps1", $"write-file {marker}|ran", "out Changed");
        PluginSchedulerSnapshot changed = await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("hello.ps1"))?.Status == PluginScheduleStatus.PendingApproval, "content change");
        Assert.Null(changed.Find(Id("hello.ps1"))!.Output);
        Assert.Equal(PluginTrustStatus.PendingApproval, _env.Trust.Check(_env.TrustService, "hello.ps1").Status);
        Assert.Equal(RefreshRequestStatus.NotApproved, scheduler.RequestRefresh(Id("hello.ps1")));
        Assert.Equal(1, _env.ProcessStarts);
        Assert.False(File.Exists(marker));

        // Approved again (a new generation), refreshed manually.
        PluginApproval second = _env.Trust.Approve(_env.TrustService, "hello.ps1");
        scheduler.NotifyTrustChanged(Id("hello.ps1"));
        await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("hello.ps1"))?.ObservedTrustStatus == PluginTrustStatus.Approved, "reapproval");
        Assert.Equal(RefreshRequestStatus.Accepted, scheduler.RequestRefresh(Id("hello.ps1")));
        PluginSchedulerSnapshot republished = await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("hello.ps1"))?.Output?.Approval == second.Identity, "republished output");
        Assert.Equal("Changed", republished.Find(Id("hello.ps1"))!.Output!.Output.Titles[0].Item.Text);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.Equal(PluginTrustStatus.StaleApproval, _env.TrustService.CheckTrust(output.Approval, Token).Status);
        Assert.Equal(2, _env.ProcessStarts);

        SchedulerStopResult result = await scheduler.StopAsync(Token);
        Assert.True(result.IsComplete);
        Assert.Equal(SchedulerLifecycle.Stopped, scheduler.Snapshot.Lifecycle);
        Assert.Null(scheduler.Snapshot.Find(Id("hello.ps1"))!.Output);
        Assert.False(scheduler.Snapshot.Discovery.WatcherActive);
    }

    [Fact]
    public async Task AnUnapprovedIntervalPluginNeverExecutes()
    {
        string marker = _env.MarkerPath("evil");
        _env.Write("evil.250ms.ps1", $"write-file {marker}|ran", "out x");
        _env.Write("sub/evil.1s.py", $"write-file {marker}|ran", "out x");
        PluginScheduler scheduler = Create(reconciliation: TimeSpan.FromSeconds(1));
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);

        // Several reconciliations (each with a trust check of every plugin) and a manual request later: nothing ran.
        await WaitForAsync(scheduler, snapshot => snapshot.Discovery.CompletedScans >= 3, "three scans");
        Assert.Equal(RefreshRequestStatus.NotApproved, scheduler.RequestRefresh(Id("evil.250ms.ps1")));
        Assert.All(scheduler.Snapshot.Plugins, plugin => Assert.Equal(PluginScheduleStatus.PendingApproval, plugin.Status));
        Assert.Equal(0, _env.ProcessStarts);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task TheWatcherFindsANewPluginWithoutPeriodicReconciliation()
    {
        PluginScheduler scheduler = Create();
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);
        Assert.Empty(scheduler.Snapshot.Plugins);
        Assert.True(scheduler.Snapshot.Discovery.WatcherActive);

        _env.Write("work/new.5s.ps1", "out new");
        PluginSchedulerSnapshot snapshot = await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("work/new.5s.ps1")) is not null, "the new plugin");
        Assert.True(snapshot.Discovery.CompletedScans >= 2);
        Assert.Equal(PluginScheduleStatus.PendingApproval, (await WaitForAsync(scheduler, s => s.Find(Id("work/new.5s.ps1"))!.ObservedTrustStatus is not null, "its trust check")).Find(Id("work/new.5s.ps1"))!.Status);

        // Removing it is noticed the same way.
        File.Delete(_env.Trust.PluginPath("work/new.5s.ps1"));
        await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("work/new.5s.ps1")) is null, "the removal");
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task AnIntervalPluginRunsRepeatedlyAndShutdownTerminatesItsProcesses()
    {
        string pidFile = _env.PidFile("holder");
        _env.Write("tick.250ms.ps1", "out tick");
        _env.Write("long.1s.ps1", $"spawn-holder {pidFile}|60000", "sleep 60000", "out never");
        _env.Trust.Approve(_env.TrustService, "tick.250ms.ps1");
        _env.Trust.Approve(_env.TrustService, "long.1s.ps1");
        PluginScheduler scheduler = Create();
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);

        // The 250 ms plugin keeps running (several completed runs), never overlapping, while the long one is active.
        await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("tick.250ms.ps1"))?.LastRun?.RunId > 4, "several runs");
        Assert.NotNull(scheduler.Snapshot.Find(Id("tick.250ms.ps1"))!.Output);
        using (Process? holder = ExecutionTestEnvironment.ProcessFromPidFile(pidFile))
        {
            Assert.NotNull(holder);
        }

        Assert.Equal(PluginActivity.Running, scheduler.Snapshot.Find(Id("long.1s.ps1"))!.Activity);

        var watch = Stopwatch.StartNew();
        SchedulerStopResult result = await scheduler.StopAsync(Token);
        Assert.True(result.IsComplete, $"outstanding: {string.Join(", ", result.OutstandingRuns)}");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(15), watch.Elapsed.ToString());
        ExecutionTestEnvironment.AssertExited(pidFile);
        PluginRunSummary cancelled = scheduler.Snapshot.Find(Id("long.1s.ps1"))!.LastRun!;
        Assert.Equal(PluginRunDisposition.Superseded, cancelled.Disposition);
        Assert.Equal(Core.Execution.PluginExecutionStatus.Cancelled, cancelled.ExecutionStatus);
        Assert.Empty(scheduler.Snapshot.CleanupBlocks);
    }

    [Fact]
    public async Task AnUnsavedInvalidationOfTheSharedTrustServiceStaysEffective()
    {
        string[] original = ["out ok"];
        _env.Write("a.ps1", original);
        _env.Trust.Approve(_env.TrustService, "a.ps1");
        PluginScheduler scheduler = Create();
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);
        Assert.Equal(PluginTrustStatus.Approved, scheduler.Snapshot.Find(Id("a.ps1"))!.ObservedTrustStatus);

        // The change is observed, but its invalidation cannot be saved (read-only store).
        File.SetAttributes(_env.Trust.StorePath, FileAttributes.ReadOnly);
        _env.Trust.OnDispose(() => File.SetAttributes(_env.Trust.StorePath, FileAttributes.Normal));
        _env.Write("a.ps1", "out changed");
        await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("a.ps1"))?.ObservedTrustStatus == PluginTrustStatus.ContentChanged, "the observed change");

        // The approved bytes return: the shared trust service, which the scheduler and the engine both use, keeps denying.
        _env.Write("a.ps1", original);
        scheduler.NotifyTrustChanged(Id("a.ps1"));
        PluginSchedulerSnapshot snapshot = await WaitForAsync(scheduler, snapshot => !snapshot.Find(Id("a.ps1"))!.TrustCheckPending, "the next check");
        Assert.Equal(PluginTrustStatus.ContentChanged, snapshot.Find(Id("a.ps1"))!.ObservedTrustStatus);
        Assert.Equal(RefreshRequestStatus.NotApproved, scheduler.RequestRefresh(Id("a.ps1")));
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task ASameLengthSameTimestampEditIsFoundByPeriodicReconciliation()
    {
        string path = _env.Write("a.ps1", "out aaaa");
        _env.Trust.Approve(_env.TrustService, "a.ps1");
        DateTime written = File.GetLastWriteTimeUtc(path);
        PluginScheduler scheduler = Create(reconciliation: TimeSpan.FromSeconds(1));
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);
        Assert.Equal(PluginTrustStatus.Approved, scheduler.Snapshot.Find(Id("a.ps1"))!.ObservedTrustStatus);

        File.WriteAllText(path, "out bbbb\n");
        File.SetLastWriteTimeUtc(path, written);
        Assert.Equal(new FileInfo(path).Length, "out aaaa\n".Length);
        await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("a.ps1"))?.ObservedTrustStatus == PluginTrustStatus.ContentChanged, "the fingerprint change");
        Assert.Equal(PluginTrustStatus.PendingApproval, _env.Trust.Check(_env.TrustService, "a.ps1").Status);
    }

    [Fact]
    public async Task AMetadataOnlyEditChangesTheFingerprint()
    {
        _env.Write("a.ps1", "# <traywisp.title>One</traywisp.title>", "out ok");
        _env.Trust.Approve(_env.TrustService, "a.ps1");
        PluginScheduler scheduler = Create();
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);
        Assert.Equal("One", scheduler.Snapshot.Find(Id("a.ps1"))!.Descriptor.DisplayTitle);

        // Only a comment changes; the helper ignores comments, so the behavior is identical, but the bytes are not.
        _env.Write("a.ps1", "# <traywisp.title>Two</traywisp.title>", "out ok");
        PluginSchedulerSnapshot changed = await WaitForAsync(scheduler, snapshot => snapshot.Find(Id("a.ps1"))?.ObservedTrustStatus == PluginTrustStatus.ContentChanged, "the change");
        Assert.Equal("Two", changed.Find(Id("a.ps1"))!.Descriptor.DisplayTitle);
        Assert.Equal(0, _env.ProcessStarts);
    }

    [Fact]
    public async Task ARootThatDisappearsIsUncertainNotRemovedAndRecovers()
    {
        _env.Write("a.ps1", "out ok");
        PluginApproval approval = _env.Trust.Approve(_env.TrustService, "a.ps1");
        PluginScheduler scheduler = Create();
        await scheduler.StartAsync(Token).WaitAsync(Bound, Token);

        string away = _env.Root + "-away";
        MoveWithRetry(_env.Root, away);
        try
        {
            scheduler.RequestRescan();
            PluginSchedulerSnapshot lost = await WaitForAsync(scheduler, snapshot => !snapshot.Discovery.RootAvailable, "the root problem");
            Assert.Equal(PluginPresence.Uncertain, lost.Find(Id("a.ps1"))!.Presence);
            Assert.False(lost.Discovery.WatcherActive);
        }
        finally
        {
            MoveWithRetry(away, _env.Root);
        }

        scheduler.RequestRescan();
        PluginSchedulerSnapshot back = await WaitForAsync(
            scheduler,
            snapshot => snapshot.Discovery.RootAvailable && snapshot.Find(Id("a.ps1")) is { Presence: PluginPresence.Present, ObservedTrustStatus: PluginTrustStatus.Approved },
            "recovery");
        Assert.True(back.Discovery.WatcherActive);
        Assert.Equal(approval.Identity, _env.TrustService.CheckTrust(_env.Root, Id("a.ps1"), Token).Approval);
    }

    private static void MoveWithRetry(string from, string to)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.Move(from, to);
                return;
            }
            catch (IOException) when (attempt < 50)
            {
                // A scan or another program may hold the directory for a moment.
                Thread.Sleep(50);
            }
        }
    }
}
