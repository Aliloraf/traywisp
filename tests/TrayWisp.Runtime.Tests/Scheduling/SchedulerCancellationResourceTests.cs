using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Scheduling;
using TrayWisp.Runtime.Tests.Execution;
using TrayWisp.Runtime.Trust;
using Xunit;

namespace TrayWisp.Runtime.Tests.Scheduling;

// The lifetime of the scheduler's cancellation sources. A wait on a token (CancellationToken.WaitHandle, as in the trust
// store's lock wait) allocates an operating-system event owned by the source; the tests read the event's handle while
// the token is in use and then check, without any garbage collection, that the handle is closed exactly when nothing
// can use the source any more, and not before.
public sealed class SchedulerCancellationResourceTests
{
    private const string Plugin = "a.5s.ps1";

    private static PluginId Id => PluginId.Create(Plugin);

    private static CancellationToken Token => SchedulerHarness.Token;

    // Records the event handle of every token the fake operations receive, by operation.
    private static ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> RecordHandles(SchedulerHarness harness)
    {
        var handles = new ConcurrentQueue<(string, SafeWaitHandle)>();
        harness.Ops.TokenObserved = (operation, token) => handles.Enqueue((operation, token.WaitHandle.SafeWaitHandle));
        return handles;
    }

    private static SafeWaitHandle Last(ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles, string operation) =>
        handles.Last(entry => entry.Operation == operation).Handle;

    private static void AssertNotFaulted(SchedulerHarness harness) =>
        Assert.DoesNotContain(harness.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted);

    [Fact]
    public async Task ANormalAttemptReleasesItsWaitHandleAfterCompletion()
    {
        await using var harness = new SchedulerHarness();
        ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles = RecordHandles(harness);
        await harness.StartAsync(Plugin);
        FakeRun run = harness.Ops.LastRun(Plugin);
        SafeWaitHandle attempt = Last(handles, "run");
        SafeWaitHandle work = Last(handles, "discover");

        // In use: kept.
        Assert.False(attempt.IsClosed);
        Assert.Equal((1, true), harness.Scheduler.UndisposedCancellationSources);

        // The output check uses the same token; after the completion the source is disposed and its event closed.
        await harness.CompleteAsync(Plugin);
        Assert.NotNull(harness.Plugin(Plugin).Output);
        Assert.Same(attempt, Last(handles, "approval"));
        Assert.True(attempt.IsClosed);
        Assert.Throws<ObjectDisposedException>(() => run.Token.WaitHandle);
        Assert.Equal((0, true), harness.Scheduler.UndisposedCancellationSources);

        // The scheduler's own source, used by reconciliation (scan and trust checks), lives while it runs.
        Assert.Same(work, Last(handles, "trust"));
        Assert.False(work.IsClosed);

        // The next run has a new source, released the same way.
        await harness.AdvanceAsync(TimeSpan.FromSeconds(5));
        SafeWaitHandle next = Last(handles, "run");
        Assert.NotSame(attempt, next);
        Assert.False(next.IsClosed);
        await harness.CompleteAsync(Plugin, RunOutcome.Failed);
        Assert.True(next.IsClosed);
        Assert.Equal((0, true), harness.Scheduler.UndisposedCancellationSources);
        AssertNotFaulted(harness);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancelledAttemptReleasesItsSourceOnlyAfterExecutionAndCallbacksEnded(bool callbacksEndFirst)
    {
        await using var harness = new SchedulerHarness();
        ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles = RecordHandles(harness);
        var callback = new Gate(observeCancellation: false);
        harness.Ops.RunsHonorCancellation = false;
        harness.Ops.CancellationCallback = () => callback.Wait(CancellationToken.None);
        await harness.StartAsync(Plugin);
        FakeRun run = harness.Ops.LastRun(Plugin);
        SafeWaitHandle handle = Last(handles, "run");

        // Superseded while running: the token is cancelled at once, and the engine's callback is held on the thread pool.
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.SetEnabled(Id, enabled: false));
        Assert.True(run.CancellationRequested);
        await callback.Entered.WaitAsync(Gate.TestTimeout, Token);
        Assert.False(handle.IsClosed);
        Assert.Equal(1, harness.Scheduler.UndisposedCancellationSources.Attempts);

        if (callbacksEndFirst)
        {
            // The delivery ended, but the engine still runs with the token.
            callback.Release();
            await harness.SettleAsync();
            Assert.Equal(1, harness.Snapshot.ActiveRuns);
            Assert.False(handle.IsClosed);
            Assert.Equal(1, harness.Scheduler.UndisposedCancellationSources.Attempts);
            run.Complete(RunOutcome.Failed);
            await harness.SettleAsync();
        }
        else
        {
            // The run ended and its slot was freed, but a callback of its cancellation still runs.
            run.Complete(RunOutcome.Failed);
            PluginSchedulerSnapshot ended = await harness.WaitForAsync(snapshot => snapshot.ActiveRuns == 0);
            Assert.Equal(PluginRunDisposition.Superseded, ended.Find(Id)!.LastRun!.Disposition);
            Assert.False(handle.IsClosed);
            Assert.Equal(1, harness.Scheduler.UndisposedCancellationSources.Attempts);
            callback.Release();
            await harness.SettleAsync();
        }

        Assert.True(handle.IsClosed);
        Assert.Equal((0, true), harness.Scheduler.UndisposedCancellationSources);
        AssertNotFaulted(harness);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposalCannotRaceWithInvalidationBeforeLaunch(bool beforeDecision)
    {
        var hold = new Gate(observeCancellation: false);
        var hooks = beforeDecision
            ? new SchedulerTestHooks { AttemptStarting = _ => hold.Wait(CancellationToken.None) }
            : new SchedulerTestHooks { EngineCalling = _ => hold.Wait(CancellationToken.None) };
        await using var harness = new SchedulerHarness(hooks: hooks);
        ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles = RecordHandles(harness);
        harness.Ops.Add(Plugin);
        harness.Ops.Approve(Plugin);
        await harness.Scheduler.StartAsync(Token);
        await hold.Entered.WaitAsync(Gate.TestTimeout, Token);
        await harness.WaitForAsync(snapshot => snapshot.ActiveRuns == 1);

        // Superseded repeatedly while held: one cancellation, the source kept while the attempt is registered.
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged(Id));
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.SetEnabled(Id, enabled: false));
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged());
        Assert.Equal((1, true), harness.Scheduler.UndisposedCancellationSources);

        hold.Release();
        PluginSchedulerSnapshot ended = await harness.WaitForAsync(snapshot => snapshot.ActiveRuns == 0);
        await harness.SettleAsync();
        Assert.Equal(PluginRunDisposition.Superseded, ended.Find(Id)!.LastRun!.Disposition);
        Assert.Equal((0, true), harness.Scheduler.UndisposedCancellationSources);
        if (beforeDecision)
        {
            // Never reached the engine.
            Assert.Equal(0, harness.Ops.EngineCalls);
        }
        else
        {
            // Reached the engine with a cancelled token, which the engine waited on: its event is closed now.
            Assert.Equal(1, harness.Ops.CancelledEngineCalls);
            Assert.True(Last(handles, "run").IsClosed);
        }

        // Later supersessions find no registered attempt and never touch the disposed source.
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged(Id));
        Assert.Equal(PluginCommandStatus.Applied, harness.Scheduler.NotifyTrustChanged());
        await harness.RescanAsync();
        Assert.Equal((0, true), harness.Scheduler.UndisposedCancellationSources);
        AssertNotFaulted(harness);
        Assert.True((await harness.Scheduler.StopAsync(Token)).IsComplete);
        Assert.Equal((0, false), harness.Scheduler.UndisposedCancellationSources);
    }

    [Fact]
    public async Task RepeatedAndConcurrentShutdownNeitherCancelsNorDisposesASourceIncorrectly()
    {
        await using var harness = new SchedulerHarness();
        ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles = RecordHandles(harness);
        var callback = new Gate(observeCancellation: false);
        harness.Ops.RunsHonorCancellation = false;
        harness.Ops.CancellationCallback = () => callback.Wait(CancellationToken.None);
        await harness.StartAsync(Plugin);
        FakeRun run = harness.Ops.LastRun(Plugin);
        SafeWaitHandle attempt = Last(handles, "run");
        SafeWaitHandle work = Last(handles, "discover");

        // Concurrent stops and disposals share one shutdown, which waits for the held cancellation callback.
        Task<SchedulerStopResult>[] stops = [.. Enumerable.Range(0, 3).Select(_ => Task.Run(() => harness.Scheduler.StopAsync(Token), Token))];
        Task[] disposals = [.. Enumerable.Range(0, 2).Select(_ => Task.Run(async () => await harness.Scheduler.DisposeAsync(), Token))];
        await callback.Entered.WaitAsync(Gate.TestTimeout, Token);
        run.Complete(RunOutcome.Failed);
        PluginSchedulerSnapshot stopping = await harness.WaitForAsync(snapshot => snapshot.ActiveRuns == 0);
        Assert.Equal(SchedulerLifecycle.Stopping, stopping.Lifecycle);

        // A caller whose wait ends early gets an incomplete result and releases nothing.
        using (var early = new CancellationTokenSource())
        {
            early.Cancel();
            SchedulerStopResult result = await harness.Scheduler.StopAsync(early.Token);
            Assert.False(result.IsComplete);
            Assert.True(result.TeardownOutstanding);
        }

        Assert.All(stops, stop => Assert.False(stop.IsCompleted));
        Assert.False(attempt.IsClosed);
        Assert.False(work.IsClosed);
        Assert.Equal((1, true), harness.Scheduler.UndisposedCancellationSources);

        callback.Release();
        SchedulerStopResult[] results = await Task.WhenAll(stops).WaitAsync(Gate.TestTimeout, Token);
        await Task.WhenAll(disposals).WaitAsync(Gate.TestTimeout, Token);
        Assert.All(results, result => Assert.True(result.IsComplete));
        Assert.True(attempt.IsClosed);
        Assert.True(work.IsClosed);
        Assert.Equal((0, false), harness.Scheduler.UndisposedCancellationSources);

        // Later calls never signal or dispose a source again.
        Assert.True((await harness.Scheduler.StopAsync(Token)).IsComplete);
        await harness.Scheduler.DisposeAsync();
        Assert.Equal(PluginCommandStatus.NotRunning, harness.Scheduler.NotifyTrustChanged());
        AssertNotFaulted(harness);
    }

    [Fact]
    public async Task StoppingBeforeStartupReleasesTheSchedulerSourceOnce()
    {
        await using var harness = new SchedulerHarness();
        Assert.Equal((0, true), harness.Scheduler.UndisposedCancellationSources);

        Assert.True((await harness.Scheduler.StopAsync(Token)).IsComplete);
        Assert.Equal((0, false), harness.Scheduler.UndisposedCancellationSources);
        Assert.True((await harness.Scheduler.StopAsync(Token)).IsComplete);
        await harness.Scheduler.DisposeAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Scheduler.StartAsync(Token));
        Assert.Equal(0, harness.Ops.Scans);
        AssertNotFaulted(harness);
    }

    [Fact]
    public async Task AnIncompleteShutdownRetainsSourcesStillInUse()
    {
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.Zero });
        ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles = RecordHandles(harness);
        harness.Ops.RunsHonorCancellation = false;
        await harness.StartAsync(Plugin);
        FakeRun run = harness.Ops.LastRun(Plugin);
        SafeWaitHandle attempt = Last(handles, "run");

        // A scan that does not observe cancellation (a blocked file system call) holds the scheduler's token.
        var scan = new Gate(observeCancellation: false);
        harness.Ops.DiscoverGate = scan;
        Assert.True(harness.Scheduler.RequestRescan());
        await scan.Entered.WaitAsync(Gate.TestTimeout, Token);
        SafeWaitHandle work = Last(handles, "discover");

        SchedulerStopResult first = await harness.Scheduler.StopAsync(Token);
        Assert.False(first.IsComplete);
        Assert.True(first.ReconciliationOutstanding);
        Assert.Equal([Id], first.OutstandingRuns);
        Assert.True(run.CancellationRequested);
        Assert.False(attempt.IsClosed);
        Assert.False(work.IsClosed);
        Assert.Equal((1, true), harness.Scheduler.UndisposedCancellationSources);

        // The scan ends; the run still uses its token, so neither source is released (the scheduler has not stopped).
        scan.Release();
        await harness.SettleAsync();
        SchedulerStopResult second = await harness.Scheduler.StopAsync(Token);
        Assert.False(second.IsComplete);
        Assert.False(second.ReconciliationOutstanding);
        Assert.Equal(SchedulerLifecycle.Stopping, second.Lifecycle);
        Assert.False(attempt.IsClosed);
        Assert.False(work.IsClosed);
        Assert.Equal((1, true), harness.Scheduler.UndisposedCancellationSources);

        // The run ends: the scheduler stops, and both sources are released.
        run.Complete(RunOutcome.Succeeded);
        await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Stopped);
        Assert.True(attempt.IsClosed);
        Assert.True(work.IsClosed);
        Assert.Equal((0, false), harness.Scheduler.UndisposedCancellationSources);
        Assert.True((await harness.Scheduler.StopAsync(Token)).IsComplete);
        AssertNotFaulted(harness);
    }

    [Fact]
    public async Task TheSchedulerSourceIsReleasedAfterACompletedShutdownThatCancelledAReconciliation()
    {
        await using var harness = new SchedulerHarness();
        ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles = RecordHandles(harness);
        await harness.StartAsync();

        // A scan waiting on the scheduler's token when shutdown begins.
        var scan = new Gate();
        harness.Ops.DiscoverGate = scan;
        Assert.True(harness.Scheduler.RequestRescan());
        await scan.Entered.WaitAsync(Gate.TestTimeout, Token);
        SafeWaitHandle work = Last(handles, "discover");
        Assert.False(work.IsClosed);

        SchedulerStopResult result = await harness.Scheduler.StopAsync(Token);
        Assert.True(result.IsComplete);
        Assert.True(work.IsClosed);
        Assert.Equal((0, false), harness.Scheduler.UndisposedCancellationSources);
        AssertNotFaulted(harness);
    }

    public static TheoryData<string> CompletionFailures => ["recovered", "unrecorded", "handler-failed"];

    [Theory]
    [MemberData(nameof(CompletionFailures))]
    public async Task AFailedCompletionReleasesTheSourceOnlyIfTheAttemptIsNoLongerRegistered(string failure)
    {
        var hooks = new SchedulerTestHooks
        {
            MaxCleanupIncidents = failure == "unrecorded" ? 0 : null,
            FaultPoint = point =>
            {
                if (point == SchedulerFaultPoint.Completion || (failure == "handler-failed" && point == SchedulerFaultPoint.CompletionRecovery))
                {
                    throw new InvalidOperationException("injected");
                }
            },
        };
        await using var harness = new SchedulerHarness(new PluginSchedulerOptions { ShutdownTimeout = TimeSpan.Zero }, hooks);
        ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> handles = RecordHandles(harness);
        await harness.StartAsync(Plugin);
        SafeWaitHandle attempt = Last(handles, "run");
        harness.Ops.LastRun(Plugin).Complete(failure == "unrecorded" ? RunOutcome.CleanupIncomplete : RunOutcome.Succeeded);
        PluginSchedulerSnapshot faulted = await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Faulted);
        SchedulerStopResult result = await harness.Scheduler.StopAsync(Token);

        if (failure == "recovered")
        {
            // The handler unregistered the attempt: nothing can use the source, and shutdown completes (the zero bound
            // returns before the teardown ended, so the end is awaited through the snapshots).
            Assert.Equal(0, faulted.ActiveRuns);
            Assert.Empty(result.OutstandingRuns);
            await harness.WaitForAsync(snapshot => snapshot.Lifecycle == SchedulerLifecycle.Stopped);
            Assert.True((await harness.Scheduler.StopAsync(Token)).IsComplete);
            Assert.True(attempt.IsClosed);
            Assert.Equal((0, false), harness.Scheduler.UndisposedCancellationSources);
        }
        else
        {
            // Still registered (and reported as outstanding): the source is kept with it, as is the scheduler's own.
            Assert.Equal(1, faulted.ActiveRuns);
            Assert.False(result.IsComplete);
            Assert.False(attempt.IsClosed);
            Assert.Equal((1, true), harness.Scheduler.UndisposedCancellationSources);
        }
    }
}

// Cancellation resources with the real trust service: the trust store's lock is held by the test while the scheduler's
// reconciliation, the engine's launch-time check and the output check wait for it, so that the store itself waits on the
// scheduler's tokens (CancellationToken.WaitHandle) and allocates their events.
[SupportedOSPlatform("windows")]
public sealed class SchedulerCancellationResourceIntegrationTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    // The token's source and the source's lazily allocated event, read without allocating it.
    private static readonly FieldInfo TokenSource = typeof(CancellationToken).GetField("_source", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly FieldInfo SourceEvent = typeof(CancellationTokenSource).GetField("_kernelEvent", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly ExecutionTestEnvironment _env = ExecutionTestEnvironment.Create();

    private static CancellationToken Token => ExecutionTestEnvironment.Token;

    public void Dispose() => _env.Dispose();

    private static ManualResetEvent? AllocatedEvent(CancellationToken token) =>
        TokenSource.GetValue(token) is CancellationTokenSource source ? (ManualResetEvent?)SourceEvent.GetValue(source) : null;

    [Fact]
    public async Task EventsAllocatedByTrustStoreContentionAreReleasedAfterTheRunAndAfterShutdown()
    {
        Assert.NotNull(TokenSource);
        Assert.NotNull(SourceEvent);
        _env.Write("hello.ps1", "out Hello");
        _env.Trust.Approve(_env.TrustService, "hello.ps1");
        PluginExecutionService execution = _env.Service();
        var ops = new ContendedOperations(new ProductionSchedulerOperations(execution, runOptions: null), _env.Trust.StorePath + ".lock");
        PluginRoot root = PluginRoot.Create(_env.Root);
        var scheduler = new PluginScheduler(ops, root, new PluginSchedulerOptions { ReconciliationInterval = TimeSpan.FromHours(1) }, TimeProvider.System, hooks: null, trackActivity: false);
        try
        {
            // The initial reconciliation's trust check waited for the store lock on the scheduler's token.
            await scheduler.StartAsync(Token).WaitAsync(Bound, Token);
            PluginId id = PluginId.Create("hello.ps1");
            Assert.Equal(PluginTrustStatus.Approved, scheduler.Snapshot.Find(id)!.ObservedTrustStatus);
            SafeWaitHandle work = ops.Handle("trust");
            Assert.False(work.IsClosed);

            // The launch-time check and the output check waited on the attempt's token.
            Assert.Equal(RefreshRequestStatus.Accepted, scheduler.RequestRefresh(id));
            PluginSchedulerSnapshot published = await WaitForAsync(scheduler, snapshot => snapshot.Find(id)?.Output is not null);
            Assert.Equal("Hello", published.Find(id)!.Output!.Output.Titles[0].Item.Text);
            SafeWaitHandle attempt = ops.Handle("run");
            Assert.Same(attempt, ops.Handle("approval"));
            Assert.NotSame(work, attempt);

            // Published means completed: the attempt's event is closed; the scheduler's stays open while it runs.
            Assert.True(attempt.IsClosed);
            Assert.False(work.IsClosed);
            Assert.Equal((0, true), scheduler.UndisposedCancellationSources);

            Assert.True((await scheduler.StopAsync(Token)).IsComplete);
            Assert.True(work.IsClosed);
            Assert.Equal((0, false), scheduler.UndisposedCancellationSources);
            Assert.DoesNotContain(scheduler.Snapshot.Diagnostics, diagnostic => diagnostic.Code == SchedulerDiagnosticCode.SchedulerFaulted);
        }
        finally
        {
            await scheduler.DisposeAsync();
        }
    }

    private static async Task<PluginSchedulerSnapshot> WaitForAsync(PluginScheduler scheduler, Func<PluginSchedulerSnapshot, bool> condition)
    {
        PluginSchedulerSnapshot snapshot = scheduler.Snapshot;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(Bound);
        while (!condition(snapshot))
        {
            snapshot = await scheduler.WaitForChangeAsync(snapshot.Revision, timeout.Token);
        }

        return snapshot;
    }

    // The production operations, with the trust store lock held around every trust check until the store has started
    // waiting on the check's token (its event exists), so each check genuinely contends for the lock.
    private sealed class ContendedOperations(SchedulerOperations inner, string lockPath) : SchedulerOperations
    {
        private readonly ConcurrentQueue<(string Operation, SafeWaitHandle Handle)> _handles = new();

        public SafeWaitHandle Handle(string operation) => _handles.Last(entry => entry.Operation == operation).Handle;

        public override RootValidation ValidateRoot(PluginRoot root, CancellationToken cancellationToken) => inner.ValidateRoot(root, cancellationToken);

        public override PluginDiscoveryResult Discover(PluginRoot root, CancellationToken cancellationToken) => inner.Discover(root, cancellationToken);

        public override IDisposable? CreateWatcher(PluginRoot root, Action<WatcherNotification> sink, out string? problem) => inner.CreateWatcher(root, sink, out problem);

        public override PluginTrustDecision CheckTrust(PluginRoot root, PluginId id, CancellationToken cancellationToken) =>
            Contend("trust", () => inner.CheckTrust(root, id, cancellationToken), cancellationToken);

        public override PluginTrustDecision CheckApproval(PluginApprovalIdentity approval, CancellationToken cancellationToken) =>
            Contend("approval", () => inner.CheckApproval(approval, cancellationToken), cancellationToken);

        public override Task<PluginExecutionResult> RunAsync(PluginRoot root, PluginId id, PluginApprovalIdentity expectedApproval, CancellationToken cancellationToken) =>
            ContendAsync("run", () => inner.RunAsync(root, id, expectedApproval, cancellationToken), cancellationToken);

        private T Contend<T>(string operation, Func<T> call, CancellationToken token)
        {
            Task release = Hold(token, out Func<bool> done);
            T result;
            try
            {
                result = call();
            }
            finally
            {
                done();
                release.Wait(Bound, CancellationToken.None);
            }

            Record(operation, token);
            return result;
        }

        private async Task<PluginExecutionResult> ContendAsync(string operation, Func<Task<PluginExecutionResult>> call, CancellationToken token)
        {
            Task release = Hold(token, out Func<bool> done);
            PluginExecutionResult result;
            try
            {
                result = await call().ConfigureAwait(false);
            }
            finally
            {
                done();
                await release.WaitAsync(Bound, CancellationToken.None).ConfigureAwait(false);
            }

            Record(operation, token);
            return result;
        }

        // Opens the lock file exclusively, then releases it on the thread pool once the store waits on the token's event
        // (or, so that a failure cannot hang the test, once the call ended or the bound elapsed).
        private Task Hold(CancellationToken token, out Func<bool> done)
        {
            var holder = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            int finished = 0;
            done = () => Interlocked.Exchange(ref finished, 1) == 0;
            return Task.Run(async () =>
            {
                var watch = Stopwatch.StartNew();
                while (AllocatedEvent(token) is null && Volatile.Read(ref finished) == 0 && watch.Elapsed < Bound)
                {
                    await Task.Delay(5, CancellationToken.None).ConfigureAwait(false);
                }

                holder.Dispose();
            });
        }

        // Called while the token's source is still in use: its event must have been allocated by the store's wait.
        private void Record(string operation, CancellationToken token)
        {
            ManualResetEvent allocated = AllocatedEvent(token) ?? throw new InvalidOperationException($"The {operation} check did not wait on its token for the trust store lock.");
            _handles.Enqueue((operation, allocated.SafeWaitHandle));
        }
    }
}
