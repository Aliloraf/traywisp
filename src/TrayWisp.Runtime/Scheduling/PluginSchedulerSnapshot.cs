using System.Collections.Immutable;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;

namespace TrayWisp.Runtime.Scheduling;

/// <summary>
/// An immutable view of a <see cref="PluginScheduler"/> at one moment. Created only by the scheduler. A newer snapshot
/// has a larger <see cref="Revision"/>; a snapshot already obtained never changes, so consumers must follow revisions
/// (see <see cref="PluginScheduler.WaitForChangeAsync"/>) and must never treat anything in a snapshot, including
/// published output, as authorization.
/// </summary>
public sealed class PluginSchedulerSnapshot
{
    internal PluginSchedulerSnapshot(
        long revision,
        DateTimeOffset createdAtUtc,
        SchedulerLifecycle lifecycle,
        PluginRoot root,
        ImmutableArray<ScheduledPlugin> plugins,
        SchedulerDiscoveryState discovery,
        ImmutableArray<PluginCleanupBlock> cleanupBlocks,
        int maxConcurrency,
        int activeRuns,
        ImmutableArray<PluginId> retiredRuns,
        bool executionHalted,
        long retainedOutputCost,
        ImmutableArray<SchedulerDiagnostic> diagnostics)
    {
        Revision = revision;
        CreatedAtUtc = createdAtUtc;
        Lifecycle = lifecycle;
        Root = root;
        Plugins = plugins;
        Discovery = discovery;
        CleanupBlocks = cleanupBlocks;
        MaxConcurrency = maxConcurrency;
        ActiveRuns = activeRuns;
        RetiredRuns = retiredRuns;
        ExecutionHalted = executionHalted;
        RetainedOutputCost = retainedOutputCost;
        Diagnostics = diagnostics;
    }

    /// <summary>Increases with every published change. Equal revisions of one scheduler are the same snapshot.</summary>
    public long Revision { get; }

    /// <summary>When the snapshot was created (UTC, informational).</summary>
    public DateTimeOffset CreatedAtUtc { get; }

    /// <summary>The scheduler's lifecycle.</summary>
    public SchedulerLifecycle Lifecycle { get; }

    /// <summary>The plugin root this scheduler manages.</summary>
    public PluginRoot Root { get; }

    /// <summary>The tracked plugins, ordered by identity.</summary>
    public ImmutableArray<ScheduledPlugin> Plugins { get; }

    /// <summary>The state of discovery, watching and reconciliation.</summary>
    public SchedulerDiscoveryState Discovery { get; }

    /// <summary>
    /// Unresolved process cleanup incidents. Each blocks its plugin identity for the scheduler's lifetime (also after the
    /// plugin is removed or reappears) and occupies one execution slot.
    /// </summary>
    public ImmutableArray<PluginCleanupBlock> CleanupBlocks { get; }

    /// <summary>The configured concurrency limit.</summary>
    public int MaxConcurrency { get; }

    /// <summary>Runs in progress, including runs of plugins that were removed or replaced meanwhile.</summary>
    public int ActiveRuns { get; }

    /// <summary>
    /// Plugins with a run still in progress whose tracked state was removed or replaced (for example a removed plugin
    /// whose cancelled run has not ended). They keep their execution slot; a reappearing plugin with the same identity is
    /// not launched until that run has ended.
    /// </summary>
    public ImmutableArray<PluginId> RetiredRuns { get; }

    /// <summary>Execution slots free now: <see cref="MaxConcurrency"/> minus active runs and unresolved cleanup incidents.</summary>
    public int AvailableSlots => Math.Max(0, MaxConcurrency - ActiveRuns - CleanupBlocks.Length);

    /// <summary>
    /// <see langword="true"/> when the scheduler launches nothing more for the rest of its lifetime: its cleanup registry
    /// is full, or it faulted.
    /// </summary>
    public bool ExecutionHalted { get; }

    /// <summary>
    /// The estimated memory, in bytes, of all output the scheduler retains, including output withheld while its plugin
    /// awaits a trust check (see <see cref="PluginScheduler.MaxRetainedOutputCost"/>). Snapshots that consumers keep are
    /// not counted.
    /// </summary>
    public long RetainedOutputCost { get; }

    /// <summary>The most recent scheduler diagnostics (<c>TW700</c>–<c>TW799</c>), oldest first, at most <see cref="PluginScheduler.MaxDiagnostics"/>.</summary>
    public ImmutableArray<SchedulerDiagnostic> Diagnostics { get; }

    /// <summary>The tracked plugin with this identity (case-insensitive), or <see langword="null"/>.</summary>
    public ScheduledPlugin? Find(PluginId id)
    {
        ArgumentNullException.ThrowIfNull(id);
        foreach (ScheduledPlugin plugin in Plugins)
        {
            if (plugin.Id == id)
            {
                return plugin;
            }
        }

        return null;
    }
}

/// <summary>The scheduler's view of one tracked plugin, as part of a <see cref="PluginSchedulerSnapshot"/>.</summary>
/// <remarks>
/// <para>The dimensions are independent: <see cref="Eligibility"/> says whether the plugin may be launched (or why
/// not), <see cref="Activity"/> whether a run is active or waiting, <see cref="Presence"/> whether the latest scan
/// confirmed it, and <see cref="Status"/> summarizes them for display.</para>
/// <para><see cref="ObservedTrustStatus"/> is the latest trust check the scheduler applied. It is informational:
/// <see cref="PluginTrustStatus.Approved"/> here never authorizes anything. Every launch is authorized by its own
/// launch-time check, and every output action needs a new check with <see cref="PublishedPluginOutput.Approval"/>.</para>
/// </remarks>
public sealed class ScheduledPlugin
{
    internal ScheduledPlugin(
        PluginDescriptor descriptor,
        PluginScheduleStatus status,
        PluginEligibility eligibility,
        PluginActivity activity,
        PluginPresence presence,
        bool isEnabled,
        PluginTrustStatus? observedTrustStatus,
        DateTimeOffset? trustObservedAtUtc,
        bool trustCheckPending,
        bool manualRefreshPending,
        DateTimeOffset? nextRefreshUtc,
        int consecutiveFailures,
        PluginRunSummary? lastRun,
        PublishedPluginOutput? output,
        long lifecycleRevision)
    {
        Descriptor = descriptor;
        Status = status;
        Eligibility = eligibility;
        Activity = activity;
        Presence = presence;
        IsEnabled = isEnabled;
        ObservedTrustStatus = observedTrustStatus;
        TrustObservedAtUtc = trustObservedAtUtc;
        TrustCheckPending = trustCheckPending;
        ManualRefreshPending = manualRefreshPending;
        NextRefreshUtc = nextRefreshUtc;
        ConsecutiveFailures = consecutiveFailures;
        LastRun = lastRun;
        Output = output;
        LifecycleRevision = lifecycleRevision;
    }

    /// <summary>The plugin's identity.</summary>
    public PluginId Id => Descriptor.Id;

    /// <summary>The latest descriptor discovery produced (identity, file name, metadata). Untrusted descriptive data.</summary>
    public PluginDescriptor Descriptor { get; }

    /// <summary>The refresh interval from the file name, or <see langword="null"/> for manual refresh only.</summary>
    public RefreshInterval? RefreshInterval => Descriptor.FileName.RefreshInterval;

    /// <summary>A single display status derived from the other dimensions.</summary>
    public PluginScheduleStatus Status { get; }

    /// <summary>Whether the plugin may be launched, or the most important reason why not.</summary>
    public PluginEligibility Eligibility { get; }

    /// <summary>Whether a run is active or waiting for a slot.</summary>
    public PluginActivity Activity { get; }

    /// <summary>Whether the latest scan confirmed the plugin.</summary>
    public PluginPresence Presence { get; }

    /// <summary><see langword="false"/> when the host disabled the plugin (in memory, for this scheduler instance).</summary>
    public bool IsEnabled { get; }

    /// <summary>The status of the latest trust check the scheduler applied, or <see langword="null"/> before the first. Informational only.</summary>
    public PluginTrustStatus? ObservedTrustStatus { get; }

    /// <summary>When that trust check was applied (UTC, informational).</summary>
    public DateTimeOffset? TrustObservedAtUtc { get; }

    /// <summary>
    /// <see langword="true"/> while a fresh trust check is requested or required and not yet applied: the plugin is not
    /// launched and its output is withheld until one is. While the root is unavailable the check waits for a scan that
    /// lists the plugin again.
    /// </summary>
    public bool TrustCheckPending { get; }

    /// <summary><see langword="true"/> when a manual refresh is pending (at most one per plugin) and the scheduler can still run it.</summary>
    public bool ManualRefreshPending { get; }

    /// <summary>
    /// When the next automatic refresh of an eligible plugin is due (UTC, for display; scheduling uses a monotonic clock),
    /// or <see langword="null"/>. A time in the past means the plugin is due and waits for a slot or the end of its run.
    /// Always <see langword="null"/> once the scheduler launches nothing more (stopping, stopped, faulted or halted).
    /// </summary>
    public DateTimeOffset? NextRefreshUtc { get; }

    /// <summary>Failed runs in a row since the last success or reset.</summary>
    public int ConsecutiveFailures { get; }

    /// <summary>The latest completed run, or <see langword="null"/>. Only the latest is kept.</summary>
    public PluginRunSummary? LastRun { get; }

    /// <summary>
    /// The current publishable output, or <see langword="null"/>. Present only while no invalidation has superseded the
    /// run that produced it, no trust decision is pending (for example while the plugin's presence or root is uncertain)
    /// and the latest trust check approved exactly <see cref="PublishedPluginOutput.Approval"/>. Its actions are inert
    /// requests.
    /// </summary>
    public PublishedPluginOutput? Output { get; }

    /// <summary>
    /// Increases whenever the plugin's state is invalidated (trust change, content change, disabling) or suspended with
    /// its runs cancelled (root unavailable or replaced).
    /// </summary>
    public long LifecycleRevision { get; }
}

/// <summary>
/// Published output of one successful run: the parsed output and the approval identity under which the run was
/// authorized and the output verified. Not a permission: activating an action requires
/// <see cref="Runtime.Trust.PluginTrustService.CheckTrust(PluginApprovalIdentity, CancellationToken)"/> with <see cref="Approval"/>.
/// </summary>
public sealed class PublishedPluginOutput
{
    internal PublishedPluginOutput(PluginOutput output, PluginApprovalIdentity approval, long runId, DateTimeOffset publishedAtUtc)
    {
        Output = output;
        Approval = approval;
        RunId = runId;
        PublishedAtUtc = publishedAtUtc;
    }

    /// <summary>The parsed titles and menu. Actions are inert.</summary>
    public PluginOutput Output { get; }

    /// <summary>The approval identity that authorized the run and that the output was verified against.</summary>
    public PluginApprovalIdentity Approval { get; }

    /// <summary>The run that produced the output.</summary>
    public long RunId { get; }

    /// <summary>When the output was published (UTC, informational).</summary>
    public DateTimeOffset PublishedAtUtc { get; }
}

/// <summary>
/// A bounded summary of one completed run. It holds no output, no standard output or error, and no approval identity:
/// publishable output exists only as <see cref="ScheduledPlugin.Output"/>.
/// </summary>
public sealed class PluginRunSummary
{
    internal PluginRunSummary(
        long runId,
        RefreshTrigger trigger,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        TimeSpan duration,
        PluginRunDisposition disposition,
        PluginExecutionStatus? executionStatus,
        PluginTrustStatus? trustStatus,
        int? exitCode,
        ProcessCleanupStatus cleanupStatus,
        ImmutableArray<ExecutionDiagnostic> executionDiagnostics,
        ImmutableArray<TrustDiagnostic> trustDiagnostics,
        ImmutableArray<OutputDiagnostic> outputDiagnostics,
        bool diagnosticsTruncated,
        ImmutableArray<SchedulerDiagnostic> schedulerDiagnostics)
    {
        RunId = runId;
        Trigger = trigger;
        StartedAtUtc = startedAtUtc;
        CompletedAtUtc = completedAtUtc;
        Duration = duration;
        Disposition = disposition;
        ExecutionStatus = executionStatus;
        TrustStatus = trustStatus;
        ExitCode = exitCode;
        CleanupStatus = cleanupStatus;
        ExecutionDiagnostics = executionDiagnostics;
        TrustDiagnostics = trustDiagnostics;
        OutputDiagnostics = outputDiagnostics;
        DiagnosticsTruncated = diagnosticsTruncated;
        SchedulerDiagnostics = schedulerDiagnostics;
    }

    /// <summary>The run's identifier, unique within the scheduler instance and increasing.</summary>
    public long RunId { get; }

    /// <summary>Why the run was started.</summary>
    public RefreshTrigger Trigger { get; }

    /// <summary>When the scheduler dispatched the run (UTC, informational).</summary>
    public DateTimeOffset StartedAtUtc { get; }

    /// <summary>When the run, including cleanup and the output trust check, ended (UTC, informational).</summary>
    public DateTimeOffset CompletedAtUtc { get; }

    /// <summary>Monotonic time from dispatch to completion.</summary>
    public TimeSpan Duration { get; }

    /// <summary>What the scheduler did with the run.</summary>
    public PluginRunDisposition Disposition { get; }

    /// <summary>
    /// The execution engine's status, or <see langword="null"/> when the engine failed unexpectedly
    /// (<see cref="PluginRunDisposition.Faulted"/>) or was never called because the run was superseded before it started
    /// (<see cref="PluginRunDisposition.Superseded"/>, <see cref="ProcessCleanupStatus.NotStarted"/>).
    /// </summary>
    public PluginExecutionStatus? ExecutionStatus { get; }

    /// <summary>The launch-time trust check's status, when it was made.</summary>
    public PluginTrustStatus? TrustStatus { get; }

    /// <summary>The process exit code, when observed.</summary>
    public int? ExitCode { get; }

    /// <summary>What became of the run's processes.</summary>
    public ProcessCleanupStatus CleanupStatus { get; }

    /// <summary>The first execution diagnostics (<c>TW600</c>–<c>TW699</c>), at most <see cref="PluginScheduler.MaxRunDiagnostics"/>.</summary>
    public ImmutableArray<ExecutionDiagnostic> ExecutionDiagnostics { get; }

    /// <summary>The first trust diagnostics (<c>TW500</c>–<c>TW599</c>), at most <see cref="PluginScheduler.MaxRunDiagnostics"/>.</summary>
    public ImmutableArray<TrustDiagnostic> TrustDiagnostics { get; }

    /// <summary>The first output parser diagnostics (<c>TW400</c>–<c>TW499</c>), at most <see cref="PluginScheduler.MaxRunDiagnostics"/>.</summary>
    public ImmutableArray<OutputDiagnostic> OutputDiagnostics { get; }

    /// <summary><see langword="true"/> when some of the run's diagnostics were not kept.</summary>
    public bool DiagnosticsTruncated { get; }

    /// <summary>Scheduler diagnostics about the run, such as why its output was not published.</summary>
    public ImmutableArray<SchedulerDiagnostic> SchedulerDiagnostics { get; }
}

/// <summary>The state of discovery, file system watching and reconciliation.</summary>
public sealed class SchedulerDiscoveryState
{
    internal SchedulerDiscoveryState(
        int completedScans,
        DateTimeOffset? lastScanCompletedAtUtc,
        bool lastScanComplete,
        bool rootAvailable,
        bool watcherActive,
        bool reconciling,
        int uncertainPlugins,
        int untrackedPlugins,
        DateTimeOffset? nextReconciliationUtc,
        ImmutableArray<PluginDiagnostic> diagnostics,
        bool diagnosticsTruncated)
    {
        CompletedScans = completedScans;
        LastScanCompletedAtUtc = lastScanCompletedAtUtc;
        LastScanComplete = lastScanComplete;
        RootAvailable = rootAvailable;
        WatcherActive = watcherActive;
        Reconciling = reconciling;
        UncertainPlugins = uncertainPlugins;
        UntrackedPlugins = untrackedPlugins;
        NextReconciliationUtc = nextReconciliationUtc;
        Diagnostics = diagnostics;
        DiagnosticsTruncated = diagnosticsTruncated;
    }

    /// <summary>Scans whose result has been applied (complete or not).</summary>
    public int CompletedScans { get; }

    /// <summary>When the latest scan was applied (UTC, informational).</summary>
    public DateTimeOffset? LastScanCompletedAtUtc { get; }

    /// <summary>
    /// Whether the latest scan was complete. Only a complete scan removes plugins; plugins missing from an incomplete one
    /// are kept as <see cref="PluginPresence.Uncertain"/>.
    /// </summary>
    public bool LastScanComplete { get; }

    /// <summary>Whether the latest validation of the plugin root succeeded.</summary>
    public bool RootAvailable { get; }

    /// <summary>Whether a file system watcher is active. Without one, changes are found by periodic reconciliation only.</summary>
    public bool WatcherActive { get; }

    /// <summary>Whether a reconciliation (scan or trust checks) is in progress.</summary>
    public bool Reconciling { get; }

    /// <summary>Tracked plugins whose presence is uncertain.</summary>
    public int UncertainPlugins { get; }

    /// <summary>Plugins in the latest scan that were not tracked because the scheduler's capacity was reached.</summary>
    public int UntrackedPlugins { get; }

    /// <summary>When the next full reconciliation is due (UTC, for display), or <see langword="null"/>.</summary>
    public DateTimeOffset? NextReconciliationUtc { get; }

    /// <summary>The latest scan's discovery diagnostics (<c>TW100</c>–<c>TW399</c>), at most <see cref="PluginScheduler.MaxDiscoveryDiagnostics"/>.</summary>
    public ImmutableArray<PluginDiagnostic> Diagnostics { get; }

    /// <summary><see langword="true"/> when some of the latest scan's diagnostics were not kept.</summary>
    public bool DiagnosticsTruncated { get; }
}

/// <summary>An unresolved process cleanup incident. It is never resolved automatically; see docs/scheduling.md.</summary>
public sealed class PluginCleanupBlock
{
    internal PluginCleanupBlock(PluginId id, long runId, DateTimeOffset blockedAtUtc)
    {
        Id = id;
        RunId = runId;
        BlockedAtUtc = blockedAtUtc;
    }

    /// <summary>The blocked plugin identity (case-insensitive).</summary>
    public PluginId Id { get; }

    /// <summary>The run whose cleanup could not be confirmed.</summary>
    public long RunId { get; }

    /// <summary>When the incident was recorded (UTC, informational).</summary>
    public DateTimeOffset BlockedAtUtc { get; }
}

/// <summary>The outcome of <see cref="PluginScheduler.StopAsync"/>.</summary>
public sealed class SchedulerStopResult
{
    internal SchedulerStopResult(SchedulerLifecycle lifecycle, ImmutableArray<PluginId> outstandingRuns, bool reconciliationOutstanding, bool teardownOutstanding, int unresolvedCleanupIncidents)
    {
        Lifecycle = lifecycle;
        OutstandingRuns = outstandingRuns;
        ReconciliationOutstanding = reconciliationOutstanding;
        TeardownOutstanding = teardownOutstanding;
        UnresolvedCleanupIncidents = unresolvedCleanupIncidents;
    }

    /// <summary>
    /// <see langword="true"/> when nothing the scheduler started or owns is still outstanding: no run, no reconciliation,
    /// no watcher or timer disposal and no cancellation delivery; the lifecycle is then
    /// <see cref="SchedulerLifecycle.Stopped"/>. It says nothing about processes of unresolved cleanup incidents, which
    /// TrayWisp cannot confirm.
    /// </summary>
    public bool IsComplete => OutstandingRuns.IsEmpty && !ReconciliationOutstanding && !TeardownOutstanding;

    /// <summary>The scheduler's lifecycle when the wait ended.</summary>
    public SchedulerLifecycle Lifecycle { get; }

    /// <summary>Plugins whose cancelled run had not ended when the wait ended.</summary>
    public ImmutableArray<PluginId> OutstandingRuns { get; }

    /// <summary>Whether a discovery scan or trust check had not ended when the wait ended.</summary>
    public bool ReconciliationOutstanding { get; }

    /// <summary>
    /// Whether the disposal of the file system watcher or the timer, or the delivery of a cancellation request (the
    /// execution engine's cancellation callbacks), had not ended when the wait ended.
    /// </summary>
    public bool TeardownOutstanding { get; }

    /// <summary>
    /// Unresolved cleanup incidents of this instance: processes started for those plugins may still be running. Stopping
    /// or restarting does not resolve them; an operator has to verify.
    /// </summary>
    public int UnresolvedCleanupIncidents { get; }
}
