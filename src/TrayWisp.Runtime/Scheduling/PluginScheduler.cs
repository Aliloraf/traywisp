using System.Collections.Immutable;
using System.Globalization;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Scheduling;

/// <summary>
/// Keeps the plugins of one plugin root discovered, trust-checked and refreshed: initial discovery, interval refreshes,
/// manual refreshes, debounced file system watching, periodic reconciliation, cancellation after relevant changes, a global
/// concurrency limit, safe publication of output and bounded shutdown. See docs/scheduling.md.
/// </summary>
/// <remarks>
/// <para><b>Authorization.</b> Every launch goes through <see cref="PluginExecutionService.RunAsync"/> semantics, with its
/// authoritative launch-time trust check and execution lease, and additionally requires the approval generation the plugin
/// was selected under. Scheduler state, including an observed <see cref="PluginTrustStatus.Approved"/>, never authorizes
/// anything. All trust checks use the execution service's own <see cref="PluginTrustService"/>.</para>
/// <para><b>Single use.</b> An instance is started once and stopped once; it cannot be restarted.</para>
/// <para><b>Scope of the guarantees.</b> No overlapping runs of one plugin and the concurrency limit hold within this
/// instance only, not across scheduler instances or processes. A host uses one scheduler per plugin root and one
/// long-lived trust service.</para>
/// <para><b>Threading.</b> All public members are thread-safe and never block on file system, trust store or process
/// work: that runs on background tasks. No consumer code is ever called by the scheduler; consumers read
/// <see cref="Snapshot"/> and wait with <see cref="WaitForChangeAsync"/>.</para>
/// </remarks>
public sealed partial class PluginScheduler : IAsyncDisposable
{
    /// <summary>Most plugins tracked at once: 500, discovery's default candidate limit. Further plugins are not scheduled (<c>TW704</c>).</summary>
    public const int MaxTrackedPlugins = 500;

    /// <summary>
    /// Most unresolved cleanup incidents recorded: 16, the largest concurrency limit. Each incident occupies one execution
    /// slot, so no more can occur; if one cannot be recorded anyway, the scheduler launches nothing more (<c>TW721</c>).
    /// </summary>
    public const int MaxCleanupIncidents = PluginSchedulerOptions.MaxConcurrencyLimit;

    /// <summary>Most plugin identities remembered from watcher notifications between two reconciliations: 256. More cause a full reconciliation.</summary>
    public const int MaxWatcherHints = 256;

    /// <summary>Most recent scheduler diagnostics kept: 64, including a final <c>TW799</c> when older ones were dropped.</summary>
    public const int MaxDiagnostics = 64;

    /// <summary>Most execution, trust and output diagnostics each kept in a <see cref="PluginRunSummary"/>: 8.</summary>
    public const int MaxRunDiagnostics = 8;

    /// <summary>Most discovery diagnostics of the latest scan kept: 64.</summary>
    public const int MaxDiscoveryDiagnostics = 64;

    /// <summary>
    /// The aggregate budget for published output: 32 MiB, as estimated by
    /// <see cref="PluginSchedulingPolicy.EstimateRetainedOutputCost"/>. Output that does not fit is not published (<c>TW731</c>).
    /// </summary>
    public const long MaxRetainedOutputCost = 32L * 1024 * 1024;

    /// <summary>How soon an incomplete scan, or a scan of an unavailable root, is retried: 5 seconds.</summary>
    public static TimeSpan IncompleteScanRetryDelay { get; } = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly SchedulerOperations _ops;
    private readonly TimeProvider _time;
    private readonly int _maxConcurrency;
    private readonly long _debounce;
    private readonly long _maxWatcherDelay;
    private readonly long _reconciliationInterval;
    private readonly long _incompleteRetry;
    private readonly long _minimumRelaunch;
    private readonly TimeSpan _shutdownTimeout;
    private readonly int _maxTracked;
    private readonly long _maxOutputCost;
    private readonly int _maxCleanupIncidents;
    private readonly bool _trackActivity;
    private readonly SchedulerTestHooks? _hooks;

    // Everything below is guarded by _gate.
    private readonly Dictionary<PluginId, PluginEntry> _plugins = [];
    private readonly Dictionary<PluginId, Attempt> _active = [];
    private readonly Dictionary<PluginId, PluginCleanupBlock> _blocks = [];
    private readonly HashSet<PluginId> _hints = [];
    private readonly Queue<SchedulerDiagnostic> _diagnostics = new();
    // Cancellation deliveries still running, each with the task that ends after its completion has been recorded.
    private readonly Dictionary<Task, Task> _deliveries = [];
    // Cancels reconciliation. Disposed once (ReleaseWork) when the scheduler has stopped: no pass and no cancellation
    // delivery can use it any more. Never signalled or read after that.
    private readonly CancellationTokenSource _work = new();
    private bool _workReleased;
    // Attempts whose cancellation source has not been disposed yet.
    private int _undisposedAttemptSources;
    private readonly TaskCompletionSource _initialPass = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private SchedulerLifecycle _lifecycle = SchedulerLifecycle.NotStarted;
    private bool _executionHalted;
    private long _attemptSequence;
    private long _revision;
    private long _droppedDiagnostics;
    private long _retainedOutputCost;
    private bool _dirty = true;
    private PluginSchedulerSnapshot _snapshot = null!;
    private TaskCompletionSource<PluginSchedulerSnapshot> _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ITimer? _timer;

    // Shutdown: the one disposal of the watcher and the timer, started by the first StopAsync.
    private Task? _teardown;
    private bool _teardownDone;

    // Reconciliation.
    private bool _reconciling;
    private Task? _pass;
    private bool _rescanRequested;
    private bool _fullTrustRequested;
    private long? _rescanRetryAt;
    private long _nextFullAt;
    private DateTimeOffset? _nextFullUtc;
    private bool _watchDirty;
    private long _watchFirst;
    private long _watchLast;
    private bool _hintsOverflowed;

    // Watching.
    private IDisposable? _watcher;
    private int _watcherGeneration;
    private RootIdentity? _watchedIdentity;
    private bool _watcherNeedsRestart;
    private bool _watcherProblemReported;

    // Discovery state for snapshots.
    private int _completedScans;
    private DateTimeOffset? _lastScanUtc;
    private bool _lastScanComplete;
    private bool _rootAvailable;
    private bool _rootProblemReported;
    private bool _incompleteReported;
    private int _untracked;
    private ImmutableArray<PluginDiagnostic> _discoveryDiagnostics = [];
    private bool _discoveryDiagnosticsTruncated;

    // Coalesced pump: 0 idle, 1 queued or running, 2 running with another pump requested.
    private int _pumpState;

    // For tests: a pulse completed whenever background work makes progress.
    private TaskCompletionSource _activity = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a scheduler for one plugin root. Nothing happens until <see cref="StartAsync"/>.</summary>
    /// <param name="executionService">
    /// The host's execution service. Every launch goes through it, and every trust check the scheduler makes uses its trust
    /// service, so scheduling and execution can never consult different trust stores.
    /// </param>
    /// <param name="rootPath">
    /// The plugin root: a fully qualified local directory path under the <see cref="PluginRoot"/> syntax rules. Only its
    /// syntax is checked here; the directory is validated before every scan.
    /// </param>
    /// <param name="options">Settings; <see cref="PluginSchedulerOptions.Default"/> when <see langword="null"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="executionService"/> or <paramref name="rootPath"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The root is not a supported path, or it overlaps the trust store directory.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    public PluginScheduler(PluginExecutionService executionService, string rootPath, PluginSchedulerOptions? options = null)
        : this(CreateOperations(executionService, rootPath, options, out PluginRoot root), root, options, TimeProvider.System, hooks: null, trackActivity: false)
    {
    }

    internal PluginScheduler(SchedulerOperations operations, PluginRoot root, PluginSchedulerOptions? options, TimeProvider timeProvider, SchedulerTestHooks? hooks, bool trackActivity = true)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(timeProvider);
        options ??= PluginSchedulerOptions.Default;
        Validate(options);
        _ops = operations;
        Root = root;
        _time = timeProvider;
        _maxConcurrency = options.MaxConcurrency;
        _debounce = ToTicks(options.WatcherDebounce);
        _maxWatcherDelay = ToTicks(options.WatcherMaxDelay);
        _reconciliationInterval = ToTicks(options.ReconciliationInterval);
        _incompleteRetry = ToTicks(IncompleteScanRetryDelay);
        _minimumRelaunch = ToTicks(PluginSchedulingPolicy.MinimumRelaunchDelay);
        _shutdownTimeout = options.ShutdownTimeout;
        _maxTracked = hooks?.MaxTrackedPlugins ?? MaxTrackedPlugins;
        _maxOutputCost = hooks?.MaxRetainedOutputCost ?? MaxRetainedOutputCost;
        _maxCleanupIncidents = hooks?.MaxCleanupIncidents ?? MaxCleanupIncidents;
        _trackActivity = trackActivity;
        _hooks = hooks;
        lock (_gate)
        {
            Commit();
        }
    }

    /// <summary>The plugin root this scheduler manages.</summary>
    public PluginRoot Root { get; }

    /// <summary>The latest snapshot. It never changes; a newer one has a larger revision.</summary>
    public PluginSchedulerSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Completes with the first snapshot whose revision is larger than <paramref name="afterRevision"/>, at once when the
    /// current one already is. Changes are coalesced: a slow consumer receives the latest snapshot, never a queue of them.
    /// Continuations never run on the scheduler's own threads or under its lock. After the scheduler has stopped no newer
    /// snapshot follows, so a waiter should pass a token.
    /// </summary>
    /// <param name="afterRevision">
    /// The <see cref="PluginSchedulerSnapshot.Revision"/> of a snapshot of this scheduler, typically the latest one the caller
    /// has processed. It must not be larger than the current revision.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="afterRevision"/> is larger than the current revision, so it does not come from this scheduler.
    /// </exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task<PluginSchedulerSnapshot> WaitForChangeAsync(long afterRevision, CancellationToken cancellationToken = default)
    {
        Task<PluginSchedulerSnapshot> changed;
        lock (_gate)
        {
            if (afterRevision > _snapshot.Revision)
            {
                // The next snapshot would not be larger: the contract could not be kept.
                throw new ArgumentOutOfRangeException(nameof(afterRevision), afterRevision, "The revision is larger than the scheduler's current revision; pass the Revision of a snapshot of this scheduler.");
            }

            if (_snapshot.Revision > afterRevision)
            {
                return Task.FromResult(_snapshot);
            }

            changed = _changed.Task;
        }

        return changed.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Starts discovery, watching and scheduling, and completes when the initial reconciliation (a scan and a trust check
    /// of each discovered plugin) has been applied, or the scheduler stopped or faulted meanwhile. Calling it again while
    /// the scheduler runs creates nothing new and waits for the same initial reconciliation.
    /// </summary>
    /// <param name="cancellationToken">Cancels only the wait; the scheduler keeps running and must still be stopped.</param>
    /// <exception cref="InvalidOperationException">The scheduler is stopping or stopped; instances cannot be restarted.</exception>
    /// <exception cref="OperationCanceledException">The wait was cancelled.</exception>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            switch (_lifecycle)
            {
                case SchedulerLifecycle.NotStarted:
                    _lifecycle = SchedulerLifecycle.Running;
                    _rescanRequested = true;
                    SetNextFullReconciliation(Now());
                    _timer = _time.CreateTimer(static state => ((PluginScheduler)state!).RequestPump(), this, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                    _dirty = true;
                    Commit();
                    break;

                case SchedulerLifecycle.Running:
                case SchedulerLifecycle.Faulted:
                    break;

                default:
                    throw new InvalidOperationException("The scheduler has been stopped. A scheduler instance cannot be restarted; create a new one.");
            }
        }

        RequestPump();
        return _initialPass.Task.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Stops the scheduler: no new work is accepted or dispatched, all published output is withdrawn, active runs and
    /// reconciliation are cancelled, the watcher and timer are disposed, and the end of all of it is awaited for at most
    /// <see cref="PluginSchedulerOptions.ShutdownTimeout"/>, measured from the call. Concurrent and repeated calls share one
    /// shutdown: each waits for the same outstanding work and gets an up-to-date result.
    /// </summary>
    /// <param name="cancellationToken">Ends the wait early; the stop itself is not undone.</param>
    /// <returns>
    /// What was still outstanding when the wait ended. Never claims completion while a run, a reconciliation, the
    /// disposal of the watcher or timer, or the delivery of a cancellation request is outstanding.
    /// </returns>
    public async Task<SchedulerStopResult> StopAsync(CancellationToken cancellationToken = default)
    {
        long started = _time.GetTimestamp();
        Task[] waits;
        lock (_gate)
        {
            if (_lifecycle == SchedulerLifecycle.NotStarted)
            {
                _lifecycle = SchedulerLifecycle.Stopped;
                _teardownDone = true;
                ReleaseWork();
                _initialPass.TrySetResult();
                _dirty = true;
                Commit();
                return StopResult();
            }

            if (_lifecycle is SchedulerLifecycle.Running or SchedulerLifecycle.Faulted)
            {
                // 1. Accept no new work. 2. Withdraw output. 3. No new dispatches. 4. Cancel active work (signalled at
                // once; callbacks run on the thread pool). 5. Stop watching and timing, on the thread pool. Waiting (6)
                // and reporting (7) follow outside the lock.
                _lifecycle = SchedulerLifecycle.Stopping;
                foreach (PluginEntry entry in _plugins.Values)
                {
                    ClearOutput(entry);
                    entry.ManualPending = false;
                    entry.Queued = false;
                    MarkChanged(entry);
                }

                CancelAllAttempts();
                Signal(_work, attempt: null);
                _initialPass.TrySetResult();
                IDisposable? watcher = TakeWatcher();
                ITimer? timer = _timer;
                _timer = null;
                _teardown = Task.Run(() => Teardown(watcher, timer), CancellationToken.None);
                _dirty = true;
            }

            waits = [.. _active.Values.Select(attempt => attempt.Task), .. _deliveries.Values, .. _pass is null ? [] : new[] { _pass }, .. _teardown is null ? [] : new[] { _teardown }];
            Commit();
        }

        TimeSpan remaining = _shutdownTimeout - _time.GetElapsedTime(started);
        if (waits.Length > 0 && remaining > TimeSpan.Zero)
        {
            try
            {
                await Task.WhenAll(waits).WaitAsync(remaining, _time, cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
            }
            catch (TimeoutException)
            {
                // Reported below as incomplete.
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Reported below as incomplete.
            }
            catch (Exception exception) when (!IsFatal(exception))
            {
                // A faulted task: its fault is observed and reported by its own handler; the state below decides.
            }
        }

        lock (_gate)
        {
            TryFinishStop();
            SchedulerStopResult result = StopResult();
            if (!result.IsComplete)
            {
                AddDiagnostic(new SchedulerDiagnostic(
                    PluginDiagnosticSeverity.Error,
                    SchedulerDiagnosticCode.ShutdownIncomplete,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"Shutdown did not finish: {result.OutstandingRuns.Length} run(s), {(result.ReconciliationOutstanding ? "a" : "no")} reconciliation and {(result.TeardownOutstanding ? "some" : "no")} watcher, timer or cancellation teardown were still outstanding. They publish and launch nothing.")));
            }

            Commit();
            return result;
        }
    }

    // Disposes what the scheduler stopped using. Runs once, on the thread pool, so that a disposal that blocks cannot
    // hold StopAsync beyond its bound; until it ends, no caller is told that shutdown completed.
    private void Teardown(IDisposable? watcher, ITimer? timer)
    {
        Exception? failure = null;
        try
        {
            watcher?.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure = exception;
        }

        try
        {
            timer?.Dispose();
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            failure ??= exception;
        }

        lock (_gate)
        {
            if (failure is not null)
            {
                Fault(failure, "the disposal of the file system watcher or the timer");
            }

            _teardownDone = true;
            _dirty = true;
            TryFinishStop();
            Commit();
        }

        Pulse();
    }

    /// <summary>Stops the scheduler (see <see cref="StopAsync"/>) and waits within the shutdown bound.</summary>
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    /// <summary>
    /// Requests one manual refresh. It uses the same authorization and execution path as automatic refreshes, never
    /// overlaps a run of the plugin, waits for a free slot and the minimum relaunch delay, and resets the plugin's periodic
    /// schedule when it completes. Duplicate requests are merged into at most one pending refresh per plugin.
    /// </summary>
    public RefreshRequestStatus RequestRefresh(PluginId pluginId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        RefreshRequestStatus status;
        lock (_gate)
        {
            status = RequestRefreshLocked(pluginId);
            Commit();
        }

        if (status == RefreshRequestStatus.Accepted)
        {
            RequestPump();
        }

        return status;
    }

    /// <summary>
    /// Requests a discovery scan as soon as no reconciliation is in progress. Discovery only: it finds added, removed and
    /// renamed plugins and changed metadata, but an edit that leaves the metadata unchanged is found only by a trust check
    /// (see <see cref="RequestReconciliation"/>). Returns <see langword="false"/> when the scheduler is not running.
    /// </summary>
    public bool RequestRescan()
    {
        lock (_gate)
        {
            if (_lifecycle != SchedulerLifecycle.Running)
            {
                return false;
            }

            _rescanRequested = true;
        }

        RequestPump();
        return true;
    }

    /// <summary>
    /// Requests a full reconciliation as soon as no reconciliation is in progress: a discovery scan, then an authoritative
    /// trust check of every tracked plugin with a supported language, exactly like the periodic one. It finds every change
    /// the trust service can observe, including an edit that leaves the metadata unchanged, without waiting for
    /// <see cref="PluginSchedulerOptions.ReconciliationInterval"/>. Unlike <see cref="NotifyTrustChanged()"/>, it withdraws
    /// output and cancels runs only for plugins whose check does not confirm their approval. Returns
    /// <see langword="false"/> when the scheduler is not running.
    /// </summary>
    public bool RequestReconciliation()
    {
        lock (_gate)
        {
            if (_lifecycle != SchedulerLifecycle.Running)
            {
                return false;
            }

            _rescanRequested = true;
            _fullTrustRequested = true;
        }

        RequestPump();
        return true;
    }

    /// <summary>
    /// Tells the scheduler that the approval of a plugin may have changed (approved, revoked, reapproved). Call it after
    /// the trust operation on the shared trust service has returned. The plugin's output is withdrawn at once, an active
    /// run is cancelled, nothing is launched until a fresh trust check has been applied, and that check is requested.
    /// </summary>
    public PluginCommandStatus NotifyTrustChanged(PluginId pluginId)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        PluginCommandStatus status;
        lock (_gate)
        {
            status = _lifecycle != SchedulerLifecycle.Running ? PluginCommandStatus.NotRunning
                : !_plugins.TryGetValue(pluginId, out PluginEntry? entry) ? PluginCommandStatus.UnknownPlugin
                : InvalidateForTrustChange(entry);
            Commit();
        }

        RequestPump();
        return status;
    }

    /// <summary>
    /// <see cref="NotifyTrustChanged(PluginId)"/> for every tracked plugin, for example after the trust store was replaced
    /// or edited outside the host.
    /// </summary>
    public PluginCommandStatus NotifyTrustChanged()
    {
        PluginCommandStatus status;
        lock (_gate)
        {
            if (_lifecycle != SchedulerLifecycle.Running)
            {
                status = PluginCommandStatus.NotRunning;
            }
            else
            {
                foreach (PluginEntry entry in _plugins.Values)
                {
                    InvalidateForTrustChange(entry);
                }

                status = PluginCommandStatus.Applied;
            }

            Commit();
        }

        RequestPump();
        return status;
    }

    /// <summary>
    /// Enables or disables a plugin, in memory, for this scheduler instance. Disabling withdraws its output, cancels an
    /// active run and drops a pending manual refresh. Enabling makes an interval plugin due at once and resets its backoff.
    /// The state is kept while the plugin is tracked, including while its presence is uncertain; a plugin that is removed
    /// by a complete scan and appears again is enabled.
    /// </summary>
    public PluginCommandStatus SetEnabled(PluginId pluginId, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(pluginId);
        PluginCommandStatus status;
        lock (_gate)
        {
            if (_lifecycle != SchedulerLifecycle.Running)
            {
                status = PluginCommandStatus.NotRunning;
            }
            else if (!_plugins.TryGetValue(pluginId, out PluginEntry? entry))
            {
                status = PluginCommandStatus.UnknownPlugin;
            }
            else if (entry.Enabled == enabled)
            {
                status = PluginCommandStatus.Unchanged;
            }
            else
            {
                entry.Enabled = enabled;
                if (enabled)
                {
                    entry.ConsecutiveFailures = 0;
                    SetDue(entry, Now(), immediate: true);
                }
                else
                {
                    entry.ManualPending = false;
                    SetDue(entry, Now(), immediate: false, clear: true);
                    Invalidate(entry, requestTrustCheck: false);
                }

                MarkChanged(entry);
                status = PluginCommandStatus.Applied;
            }

            Commit();
        }

        RequestPump();
        return status;
    }

    // Completes when no pump, reconciliation, dispatch or completion is in progress: every active attempt is inside the
    // (possibly blocked) execution engine. For deterministic tests only.
    internal async Task WhenIdleAsync()
    {
        while (true)
        {
            Task pulse;
            lock (_gate)
            {
                bool idle = Volatile.Read(ref _pumpState) == 0
                    && !_reconciling
                    && _deliveries.Count == 0
                    && _active.Values.All(attempt => attempt.Phase == AttemptPhase.Running && attempt.RunTask is { IsCompleted: false });
                if (idle)
                {
                    return;
                }

                pulse = _activity.Task;
            }

            await pulse.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
    }

    // The cancellation sources not disposed yet: the number of attempts' sources, and whether the scheduler's own. For
    // tests only.
    internal (int Attempts, bool Work) UndisposedCancellationSources
    {
        get
        {
            lock (_gate)
            {
                return (_undisposedAttemptSources, !_workReleased);
            }
        }
    }

    private static ProductionSchedulerOperations CreateOperations(PluginExecutionService executionService, string rootPath, PluginSchedulerOptions? options, out PluginRoot root)
    {
        ArgumentNullException.ThrowIfNull(executionService);
        ArgumentNullException.ThrowIfNull(rootPath);
        if (!PluginRoot.TryCreate(rootPath, out PluginRoot? parsed))
        {
            throw new ArgumentException("The plugin root must be a fully qualified local directory path (see PluginRoot).", nameof(rootPath));
        }

        string store = executionService.TrustService.StorePath;
        int separator = store.LastIndexOf('\\');
        if (PluginRoot.TryCreate(separator == 2 ? store[..3] : store[..separator], out PluginRoot? storeDirectory) && parsed.Overlaps(storeDirectory))
        {
            throw new ArgumentException("The plugin root overlaps the trust store directory. Keep plugins and the trust store apart.", nameof(rootPath));
        }

        TimeSpan? timeout = options?.RunOptions?.Timeout;
        if (timeout is { } value && (value < PluginExecutionOptions.MinTimeout || value > PluginExecutionOptions.MaxTimeout))
        {
            throw new ArgumentOutOfRangeException(nameof(options), value, "The run timeout must be between PluginExecutionOptions.MinTimeout and PluginExecutionOptions.MaxTimeout.");
        }

        root = parsed;
        return new ProductionSchedulerOperations(executionService, options?.RunOptions);
    }

    private static void Validate(PluginSchedulerOptions options)
    {
        if (options.MaxConcurrency is < PluginSchedulerOptions.MinConcurrency or > PluginSchedulerOptions.MaxConcurrencyLimit)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxConcurrency, "MaxConcurrency must be between 1 and 16.");
        }

        if (options.WatcherDebounce < TimeSpan.Zero || options.WatcherDebounce > PluginSchedulerOptions.MaxWatcherDebounce)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WatcherDebounce, "WatcherDebounce must be between zero and 10 seconds.");
        }

        if (options.WatcherMaxDelay < options.WatcherDebounce || options.WatcherMaxDelay > PluginSchedulerOptions.MaxWatcherMaxDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.WatcherMaxDelay, "WatcherMaxDelay must be at least WatcherDebounce and at most 60 seconds.");
        }

        if (options.ReconciliationInterval < PluginSchedulerOptions.MinReconciliationInterval || options.ReconciliationInterval > PluginSchedulerOptions.MaxReconciliationInterval)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ReconciliationInterval, "ReconciliationInterval must be between 1 second and 1 hour.");
        }

        if (options.ShutdownTimeout < TimeSpan.Zero || options.ShutdownTimeout > PluginSchedulerOptions.MaxShutdownTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ShutdownTimeout, "ShutdownTimeout must be between zero and 5 minutes.");
        }
    }

    private RefreshRequestStatus RequestRefreshLocked(PluginId pluginId)
    {
        if (_lifecycle == SchedulerLifecycle.Faulted || (_lifecycle == SchedulerLifecycle.Running && _executionHalted))
        {
            return RefreshRequestStatus.ExecutionHalted;
        }

        if (_lifecycle != SchedulerLifecycle.Running)
        {
            return RefreshRequestStatus.NotRunning;
        }

        if (!_plugins.TryGetValue(pluginId, out PluginEntry? entry))
        {
            return RefreshRequestStatus.UnknownPlugin;
        }

        if (PluginSchedulingPolicy.GetManualRefreshRejection(Eligibility(entry)) is { } rejection)
        {
            return rejection;
        }

        if (entry.ManualPending)
        {
            return RefreshRequestStatus.Coalesced;
        }

        entry.ManualPending = true;
        entry.ManualRequestedTimestamp = Now();
        MarkChanged(entry);
        return RefreshRequestStatus.Accepted;
    }

    // Lifecycle -------------------------------------------------------------------------------------------------------

    // Stopped only when nothing the scheduler owns is outstanding: no run, no reconciliation, no teardown and no
    // cancellation delivery. Only then can nothing use the scheduler's own cancellation source any more.
    private void TryFinishStop()
    {
        if (_lifecycle == SchedulerLifecycle.Stopping && _active.Count == 0 && !_reconciling && _teardownDone && _deliveries.Count == 0)
        {
            _lifecycle = SchedulerLifecycle.Stopped;
            ReleaseWork();
            _dirty = true;
        }
    }

    // Called under the lock when the scheduler has stopped (or was stopped before it started): no reconciliation pass
    // runs or can start, and no delivery of its cancellation is running.
    private void ReleaseWork()
    {
        if (!_workReleased)
        {
            _workReleased = true;
            DisposeSource(_work);
        }
    }

    // Disposes an attempt's cancellation source once nothing can use it: its worker has ended every use of the token, no
    // delivery of its cancellation is running, and it is no longer registered as active (so no later supersession,
    // fault or shutdown can signal it). An attempt that stays registered after a failed completion keeps its source.
    // Called under the lock; disposal runs no callbacks and only releases the source's event handle, if one was
    // allocated.
    private void TryReleaseCancellation(Attempt attempt)
    {
        if (attempt.CancellationReleased
            || !attempt.WorkerEnded
            || attempt.CancellationDelivering
            || (_active.TryGetValue(attempt.PluginId, out Attempt? active) && ReferenceEquals(active, attempt)))
        {
            return;
        }

        attempt.CancellationReleased = true;
        _undisposedAttemptSources--;
        DisposeSource(attempt.Cancellation);
    }

    // Disposes a cancellation source that nothing uses any more, including its event. After a CancelAsync that found no
    // callback to run, the runtime leaves the source in its "notifying" state (observed on .NET 10), and Dispose then
    // does not close the event, leaving it to finalization. So the event of a cancelled source is closed explicitly first;
    // if it was never allocated, this allocates and closes one at once. Safe only because no callback of the source can
    // still be running: CancelAsync's delivery has ended or found none.
    private static void DisposeSource(CancellationTokenSource source)
    {
        if (source.IsCancellationRequested)
        {
            source.Token.WaitHandle.Dispose();
        }

        source.Dispose();
    }

    private SchedulerStopResult StopResult() =>
        new(_lifecycle, [.. _active.Keys.Order()], _reconciling, _lifecycle != SchedulerLifecycle.Stopped && (!_teardownDone || _deliveries.Count > 0), _blocks.Count);

    // An unexpected internal failure: fail closed. Nothing more is launched or published; active work is cancelled.
    private void Fault(Exception exception, string where)
    {
        AddDiagnostic(new SchedulerDiagnostic(
            PluginDiagnosticSeverity.Error,
            SchedulerDiagnosticCode.SchedulerFaulted,
            string.Create(
                CultureInfo.InvariantCulture,
                $"An unexpected {exception.GetType().FullName} (HRESULT 0x{exception.HResult:X8}) occurred in {where}. The scheduler launches and publishes nothing more and must be stopped.")));
        _executionHalted = true;
        if (_lifecycle == SchedulerLifecycle.Running)
        {
            _lifecycle = SchedulerLifecycle.Faulted;
            foreach (PluginEntry entry in _plugins.Values)
            {
                ClearOutput(entry);
                entry.ManualPending = false;
                MarkChanged(entry);
            }

            CancelAllAttempts();
            Signal(_work, attempt: null);
            _initialPass.TrySetResult();
        }

        _dirty = true;
    }

    private void FaultFromBackground(Exception exception, string where)
    {
        lock (_gate)
        {
            Fault(exception, where);
            Commit();
        }
    }

    // Observes a background task's unexpected fault, including one raised after its own exception handling (for example
    // by the handler of a failed completion): the scheduler faults instead of losing the failure, and a reconciliation
    // pass that ended that way is no longer reported as in progress, and the cancellation source of a run that ended that
    // way is released if nothing can use it any more.
    private void ObserveFaults(Task task, string where, Attempt? attempt = null) =>
        task.ContinueWith(
            static (completed, state) =>
            {
                (PluginScheduler scheduler, string where, Attempt? attempt) = ((PluginScheduler, string, Attempt?))state!;
                scheduler.BackgroundTaskFaulted(completed, where, attempt);
            },
            (this, where, attempt),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted,
            TaskScheduler.Default);

    private void BackgroundTaskFaulted(Task task, string where, Attempt? attempt)
    {
        Exception exception = task.Exception!.InnerException ?? task.Exception;
        try
        {
            lock (_gate)
            {
                Fault(exception, where);
                if (ReferenceEquals(_pass, task))
                {
                    EndPass();
                }

                if (attempt is not null)
                {
                    TryReleaseCancellation(attempt);
                }

                _dirty = true;
                TryFinishStop();
                Commit();
            }
        }
        catch (Exception secondary) when (!IsFatal(secondary))
        {
            // Nothing more can be done safely; execution is already halted if Fault got that far.
        }

        Pulse();
    }

    private static bool IsFatal(Exception exception) => exception is OutOfMemoryException;

    private void CancelAllAttempts()
    {
        foreach (Attempt attempt in _active.Values)
        {
            RequestCancel(attempt);
        }
    }

    private void RequestCancel(Attempt attempt)
    {
        // A released source belongs to an attempt that has ended: there is nothing left to cancel, and it must not be used.
        if (!attempt.CancelRequested && !attempt.CancellationReleased)
        {
            attempt.CancelRequested = true;
            Signal(attempt.Cancellation, attempt);
            if (_plugins.TryGetValue(attempt.PluginId, out PluginEntry? entry))
            {
                MarkChanged(entry);
            }
        }
    }

    // Requests cancellation under the lock. CancelAsync marks the token cancelled before it returns, so no worker can pass
    // it to the engine uncancelled afterwards, and runs the token's callbacks (the execution engine's) on the thread pool,
    // never under the lock. A delivery still running is tracked: shutdown waits for it, the scheduler does not count as
    // stopped before it ended, and its failure faults the scheduler. A source is never disposed while a delivery of it
    // runs: attempt identifies the attempt whose source it is (null for the scheduler's own).
    private void Signal(CancellationTokenSource source, Attempt? attempt)
    {
        Task delivery = source.CancelAsync();
        if (delivery.IsCompletedSuccessfully)
        {
            return;
        }

        if (attempt is not null)
        {
            attempt.CancellationDelivering = true;
        }

        // The continuation takes the lock held here, so it is registered before it can remove itself. Waiting for it
        // rather than for the delivery means that its completion has been recorded when the wait ends.
        _deliveries[delivery] = delivery.ContinueWith(
            static (completed, state) =>
            {
                (PluginScheduler scheduler, Attempt? attempt) = ((PluginScheduler, Attempt?))state!;
                scheduler.DeliveryEnded(completed, attempt);
            },
            (this, attempt),
            CancellationToken.None,
            TaskContinuationOptions.None,
            TaskScheduler.Default);
    }

    private void DeliveryEnded(Task delivery, Attempt? attempt)
    {
        lock (_gate)
        {
            _deliveries.Remove(delivery);
            if (attempt is not null)
            {
                // Every callback has returned: the source can be released once its attempt has ended too.
                attempt.CancellationDelivering = false;
                TryReleaseCancellation(attempt);
            }

            if (delivery.Exception is { } failure)
            {
                Fault(failure.InnerException ?? failure, "the delivery of a cancellation request");
            }

            _dirty = true;
            TryFinishStop();
            Commit();
        }

        Pulse();
    }

    // Snapshots -------------------------------------------------------------------------------------------------------

    private void MarkChanged(PluginEntry entry)
    {
        entry.Cached = null;
        _dirty = true;
    }

    private void MarkAllChanged()
    {
        foreach (PluginEntry entry in _plugins.Values)
        {
            MarkChanged(entry);
        }
    }

    // Publishes a new snapshot if anything changed since the last one. Called at the end of every locked update.
    private void Commit()
    {
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        _revision++;
        PluginSchedulerSnapshot snapshot = BuildSnapshot();
        Volatile.Write(ref _snapshot, snapshot);
        TaskCompletionSource<PluginSchedulerSnapshot> changed = _changed;
        _changed = new TaskCompletionSource<PluginSchedulerSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        changed.TrySetResult(snapshot);
    }

    private PluginSchedulerSnapshot BuildSnapshot()
    {
        ImmutableArray<ScheduledPlugin> plugins = [.. _plugins.Values.OrderBy(entry => entry.Id).Select(entry => entry.Cached ??= BuildPlugin(entry))];
        ImmutableArray<PluginId> retired = [.. _active.Values.Where(attempt => !IsTracked(attempt.Entry)).Select(attempt => attempt.PluginId).Order()];
        var discovery = new SchedulerDiscoveryState(
            _completedScans,
            _lastScanUtc,
            _lastScanComplete,
            _rootAvailable,
            _watcher is not null,
            _reconciling,
            _plugins.Values.Count(entry => entry.Presence == PluginPresence.Uncertain),
            _untracked,
            _lifecycle == SchedulerLifecycle.Running ? _nextFullUtc : null,
            _discoveryDiagnostics,
            _discoveryDiagnosticsTruncated);
        ImmutableArray<SchedulerDiagnostic> diagnostics = _droppedDiagnostics == 0
            ? [.. _diagnostics]
            : [
                new SchedulerDiagnostic(PluginDiagnosticSeverity.Info, SchedulerDiagnosticCode.DiagnosticLimitReached, string.Create(CultureInfo.InvariantCulture, $"{_droppedDiagnostics} earlier scheduler diagnostic(s) were dropped.")),
                .. _diagnostics,
            ];
        return new PluginSchedulerSnapshot(
            _revision,
            _time.GetUtcNow(),
            _lifecycle,
            Root,
            plugins,
            discovery,
            [.. _blocks.Values.OrderBy(block => block.Id)],
            _maxConcurrency,
            _active.Count,
            retired,
            _executionHalted || _lifecycle == SchedulerLifecycle.Faulted,
            _retainedOutputCost,
            diagnostics);
    }

    private ScheduledPlugin BuildPlugin(PluginEntry entry)
    {
        // Once the scheduler launches nothing more (stopping, stopped, faulted, halted), no plugin is shown as queued or
        // with a next refresh time, so a snapshot never implies that execution will continue.
        bool live = _lifecycle == SchedulerLifecycle.Running && !_executionHalted;
        PluginEligibility eligibility = Eligibility(entry);
        PluginActivity activity = _active.TryGetValue(entry.Id, out Attempt? attempt) && ReferenceEquals(attempt.Entry, entry)
            ? (attempt.CancelRequested ? PluginActivity.Cancelling : PluginActivity.Running)
            : entry.Queued && live ? PluginActivity.Queued : PluginActivity.None;
        return new ScheduledPlugin(
            entry.Descriptor,
            PluginSchedulingPolicy.GetStatus(eligibility, activity, entry.LastRun?.Disposition),
            eligibility,
            activity,
            entry.Presence,
            entry.Enabled,
            entry.Trust,
            entry.TrustObservedAtUtc,
            entry.TrustCheckRequested || entry.AwaitingTrustCheck,
            entry.ManualPending && live,
            entry.DueTimestamp is null || eligibility != PluginEligibility.Eligible || !live ? null : entry.DueUtc,
            entry.ConsecutiveFailures,
            entry.LastRun,
            IsPublishable(entry) ? entry.Output : null,
            entry.Revision);
    }

    private bool IsTracked(PluginEntry entry) => _plugins.TryGetValue(entry.Id, out PluginEntry? current) && ReferenceEquals(current, entry);

    private PluginEligibility Eligibility(PluginEntry entry) =>
        PluginSchedulingPolicy.EvaluateEligibility(new PluginEligibilityFacts(
            _blocks.ContainsKey(entry.Id),
            entry.LanguageSupported,
            entry.Enabled,
            entry.Presence,
            entry.AwaitingTrustCheck,
            entry.Trust));

    private void AddDiagnostic(SchedulerDiagnostic diagnostic)
    {
        // One slot is kept for the TW799 summary.
        while (_diagnostics.Count >= MaxDiagnostics - 1)
        {
            _diagnostics.Dequeue();
            _droppedDiagnostics++;
        }

        _diagnostics.Enqueue(diagnostic);
        _dirty = true;
    }

    private void Pulse()
    {
        if (!_trackActivity)
        {
            return;
        }

        TaskCompletionSource pulse;
        lock (_gate)
        {
            pulse = _activity;
            _activity = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        pulse.TrySetResult();
    }

    // Time ------------------------------------------------------------------------------------------------------------

    private long Now() => _time.GetTimestamp();

    // Converts a duration to monotonic timestamp units without overflow (30 days at a 1 GHz timestamp frequency fits).
    private long ToTicks(TimeSpan duration) => (long)((Int128)duration.Ticks * _time.TimestampFrequency / TimeSpan.TicksPerSecond);

    private TimeSpan ToTimeSpan(long timestampDelta) => TimeSpan.FromTicks((long)((Int128)timestampDelta * TimeSpan.TicksPerSecond / _time.TimestampFrequency));

    private DateTimeOffset UtcAt(long timestamp, long now) => _time.GetUtcNow() + ToTimeSpan(timestamp - now);

    private void SetNextFullReconciliation(long now)
    {
        _nextFullAt = now + _reconciliationInterval;
        _nextFullUtc = UtcAt(_nextFullAt, now);
        _dirty = true;
    }

    private void SetDue(PluginEntry entry, long now, bool immediate, bool clear = false, TimeSpan? delay = null)
    {
        if (clear || entry.Interval is null)
        {
            entry.DueTimestamp = null;
            entry.DueUtc = null;
        }
        else
        {
            long due = immediate ? now : now + ToTicks(delay ?? entry.Interval.Duration);
            entry.DueTimestamp = due;
            entry.DueUtc = UtcAt(due, now);
        }

        MarkChanged(entry);
    }
}
