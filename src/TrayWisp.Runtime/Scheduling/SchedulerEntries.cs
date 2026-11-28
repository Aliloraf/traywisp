using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Scheduling;

// The mutable state of one tracked plugin. Owned by PluginScheduler and only read or written under its lock.
internal sealed class PluginEntry(PluginDescriptor descriptor)
{
    public PluginDescriptor Descriptor { get; set; } = descriptor;

    public PluginId Id => Descriptor.Id;

    public RefreshInterval? Interval => Descriptor.FileName.RefreshInterval;

    public bool LanguageSupported => Descriptor.FileName.ExecutionSupport == PluginExecutionSupport.Supported;

    public bool Enabled { get; set; } = true;

    public PluginPresence Presence { get; set; } = PluginPresence.Present;

    // Increased by every invalidation and every suspension that cancels runs. An attempt dispatched under an older revision
    // is obsolete.
    public long Revision { get; set; }

    // The latest applied trust observation. Informational: it decides eligibility, never authorization.
    public PluginTrustStatus? Trust { get; set; }

    // The approval of that observation, only when it was Approved.
    public PluginApprovalIdentity? Approval { get; set; }

    public DateTimeOffset? TrustObservedAtUtc { get; set; }

    // No trust check has been applied since discovery or the latest invalidation or suspension: not eligible, and its
    // output withheld, until one is.
    public bool AwaitingTrustCheck { get; set; } = true;

    // A trust check is requested and not yet taken by a reconciliation pass.
    public bool TrustCheckRequested { get; set; } = true;

    // The version of the entry's trust state: increased by every applied trust observation, every invalidation and every
    // suspension, whether or not another check is requested. A check that started under an older epoch is stale: it can
    // only take away (see ApplyObservation), never overwrite a newer decision.
    public long TrustEpoch { get; set; }

    // Monotonic timestamp at which the next automatic run is due, or null.
    public long? DueTimestamp { get; set; }

    public DateTimeOffset? DueUtc { get; set; }

    public bool ManualPending { get; set; }

    public long ManualRequestedTimestamp { get; set; }

    public int ConsecutiveFailures { get; set; }

    // When the latest attempt for this identity ended, for the minimum relaunch delay.
    public long? LastCompletedTimestamp { get; set; }

    // Due (or manually requested) but waiting for a slot or the minimum relaunch delay.
    public bool Queued { get; set; }

    public PluginRunSummary? LastRun { get; set; }

    public PublishedPluginOutput? Output { get; set; }

    public long OutputCost { get; set; }

    // The snapshot of this entry, rebuilt only after it changed.
    public ScheduledPlugin? Cached { get; set; }
}

internal enum AttemptPhase
{
    // Dispatched; the execution engine has not been called yet.
    Starting,

    // The execution engine is running the plugin.
    Running,

    // The engine returned; the output check and the completion are in progress.
    Finishing,
}

// One dispatched run. It keeps its execution slot, and its plugin identity, until it has completed, even when the entry it
// was dispatched for is removed or replaced.
internal sealed class Attempt(long id, PluginEntry entry, long revision, PluginApprovalIdentity expected, RefreshTrigger trigger, long startTimestamp, DateTimeOffset startedAtUtc)
{
    public long Id { get; } = id;

    public PluginId PluginId { get; } = entry.Id;

    // The entry and revision the attempt was dispatched for. A completion applies only to exactly that entry and revision.
    public PluginEntry Entry { get; } = entry;

    public long Revision { get; } = revision;

    // The approval the plugin was selected under. The launch-time check must find exactly this approval.
    public PluginApprovalIdentity Expected { get; } = expected;

    // The entry's trust epoch at dispatch, before the engine's launch-time check started.
    public long LaunchEpoch { get; } = entry.TrustEpoch;

    // The entry's trust epoch when the output check started.
    public long VerifyEpoch { get; set; }

    public RefreshTrigger Trigger { get; } = trigger;

    public long StartTimestamp { get; } = startTimestamp;

    public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;

    // Owned by the scheduler, which disposes it exactly once (PluginScheduler.TryReleaseCancellation, under its lock) when
    // nothing can use it any more. It must be disposed: although it has no timer and no linked token, a wait on its token
    // (CancellationToken.WaitHandle, as in the trust store's lock wait) allocates an operating-system event that only
    // disposal releases deterministically. See "Cancellation resources" in docs/scheduling.md.
    public CancellationTokenSource Cancellation { get; } = new();

    // The flags below are guarded by the scheduler's lock.
    public bool CancelRequested { get; set; }

    // A CancelAsync of Cancellation whose callbacks are still running on the thread pool.
    public bool CancellationDelivering { get; set; }

    // The worker has ended every use of the token: the engine call and the output trust check have returned.
    public bool WorkerEnded { get; set; }

    // Cancellation has been disposed. Nothing may signal or read it again.
    public bool CancellationReleased { get; set; }

    public Task Task { get; set; } = Task.CompletedTask;

    public Task? RunTask { get; set; }

    private int _phase;

    public AttemptPhase Phase
    {
        get => (AttemptPhase)Volatile.Read(ref _phase);
        set => Volatile.Write(ref _phase, (int)value);
    }
}

// What one reconciliation pass does.
internal sealed record ReconciliationWork(bool Rescan, bool FullTrust, bool Periodic);

// Deterministic seams for tests. Never set in production.
internal sealed class SchedulerTestHooks
{
    // Replaces the number of tracked plugins, so capacity can be tested with few plugins.
    public int? MaxTrackedPlugins { get; init; }

    // Replaces the aggregate output budget.
    public long? MaxRetainedOutputCost { get; init; }

    // Replaces the capacity of the cleanup registry, so its fail-closed behavior can be tested.
    public int? MaxCleanupIncidents { get; init; }

    // Called on an attempt's worker after dispatch, before the scheduler decides whether the attempt still starts.
    public Action<PluginId>? AttemptStarting { get; init; }

    // Called on an attempt's worker after that decision, immediately before the execution engine is called.
    public Action<PluginId>? EngineCalling { get; init; }

    // Called at named internal steps (SchedulerFaultPoint); a test makes it throw to inject an unexpected failure.
    public Action<string>? FaultPoint { get; init; }
}

// Steps at which SchedulerTestHooks.FaultPoint is called.
internal static class SchedulerFaultPoint
{
    // Under the lock, at the start of a run's completion.
    public const string Completion = "completion";

    // Under the lock, at the start of the handler of a failed completion.
    public const string CompletionRecovery = "completion-recovery";

    // Under the lock, at the start of a reconciliation pass's finalization.
    public const string ReconciliationEnd = "reconciliation-end";
}
