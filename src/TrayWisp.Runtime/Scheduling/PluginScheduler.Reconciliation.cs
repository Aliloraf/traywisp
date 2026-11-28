using System.Collections.Immutable;
using System.Globalization;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Plugins;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Scheduling;

// Reconciliation: root validation, discovery, watching and the scheduler's own trust checks. At most one pass runs at a
// time, on a background task, and applies each result under the lock as soon as it is available. Because passes never
// overlap, a scan can never be applied after a newer one. Trust observations are versioned per plugin (TrustEpoch): every
// applied observation, invalidation and suspension advances the version, and a check that started under an older one can
// only take away (see ApplyObservation).
public sealed partial class PluginScheduler
{
    private void StartReconciliationIfDue(long now)
    {
        if (_reconciling)
        {
            return;
        }

        bool watcherDue = _watchDirty && (now >= _watchLast + _debounce || now >= _watchFirst + _maxWatcherDelay);
        bool periodic = now >= _nextFullAt;
        bool retry = _rescanRetryAt is long retryAt && now >= retryAt;
        bool rescan = _rescanRequested || watcherDue || periodic || retry;
        bool fullTrust = _fullTrustRequested || periodic || (watcherDue && _hintsOverflowed);
        bool checks = fullTrust || (watcherDue && _hints.Count > 0) || _plugins.Values.Any(entry => entry.TrustCheckRequested && entry.LanguageSupported);
        if (!rescan && !checks)
        {
            return;
        }

        if (watcherDue)
        {
            _watchDirty = false;
            foreach (PluginId hint in _hints)
            {
                if (_plugins.TryGetValue(hint, out PluginEntry? entry))
                {
                    RequestTrustCheck(entry);
                }
            }

            _hints.Clear();
            _hintsOverflowed = false;
        }

        if (rescan)
        {
            _rescanRequested = false;
            _rescanRetryAt = null;
        }

        _fullTrustRequested = false;
        _reconciling = true;
        _dirty = true;
        // The token is taken here, under the lock, while the source is certainly not disposed: it is disposed only after
        // the scheduler stopped, which requires this pass to have ended.
        _pass = RunReconciliationAsync(new ReconciliationWork(rescan, fullTrust, periodic), _work.Token);
        ObserveFaults(_pass, "reconciliation");
    }

    private async Task RunReconciliationAsync(ReconciliationWork work, CancellationToken cancellationToken)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try
        {
            // While the root is unavailable nothing can be verified: no trust check is made, and every plugin stays
            // suspended until a scan lists it again.
            if (!work.Rescan || Rescan(cancellationToken))
            {
                CheckTrust(work.FullTrust, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stopping or faulted.
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            FaultFromBackground(exception, "reconciliation");
        }

        try
        {
            lock (_gate)
            {
                _hooks?.FaultPoint?.Invoke(SchedulerFaultPoint.ReconciliationEnd);
                if (work.Periodic && _lifecycle == SchedulerLifecycle.Running)
                {
                    SetNextFullReconciliation(Now());
                }

                EndPass();

                // Requested before the lock is released, so the pass never looks finished without its follow-up decision.
                RequestPump();
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            // The finalization failed part-way: fail closed, and still end the pass, so that it is never reported as
            // outstanding forever. A failure here is observed by ObserveFaults.
            lock (_gate)
            {
                Fault(exception, "the end of a reconciliation");
                EndPass();
            }
        }

        Pulse();
    }

    private void EndPass()
    {
        _reconciling = false;
        _pass = null;
        _initialPass.TrySetResult();
        _dirty = true;
        TryFinishStop();
        Commit();
    }

    // Returns false when the root could not be validated or scanned.
    private bool Rescan(CancellationToken cancellationToken)
    {
        using RootValidation validation = _ops.ValidateRoot(Root, cancellationToken);
        if (validation.Problem is not null)
        {
            IDisposable? stale;
            lock (_gate)
            {
                stale = TakeWatcher();
                ApplyRootUnavailable(validation.Problem);
                Commit();
            }

            stale?.Dispose();
            return false;
        }

        // The watcher starts before the scan, so changes made during the scan are noticed.
        EnsureWatcher(validation.Identity);
        PluginDiscoveryResult result;
        try
        {
            result = _ops.Discover(Root, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            lock (_gate)
            {
                ApplyRootUnavailable(new SchedulerDiagnostic(
                    PluginDiagnosticSeverity.Error,
                    SchedulerDiagnosticCode.DiscoveryFailed,
                    string.Create(CultureInfo.InvariantCulture, $"Discovery of {TrustPaths.Quote(Root.Value)} failed ({exception.GetType().Name}, HRESULT 0x{exception.HResult:X8}). Known plugins are kept as uncertain and suspended.")));
                Commit();
            }

            return false;
        }

        lock (_gate)
        {
            if (_lifecycle == SchedulerLifecycle.Running)
            {
                ApplyDiscovery(result);
            }

            Commit();
        }

        RequestPump();
        return true;
    }

    private void CheckTrust(bool full, CancellationToken cancellationToken)
    {
        List<PluginEntry> batch;
        lock (_gate)
        {
            batch = [.. _plugins.Values.Where(entry => entry.LanguageSupported && (full || entry.TrustCheckRequested)).OrderBy(entry => entry.Id)];
        }

        foreach (PluginEntry entry in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            long epoch;
            lock (_gate)
            {
                if (_lifecycle != SchedulerLifecycle.Running || !IsTracked(entry))
                {
                    continue;
                }

                // Taken immediately before the check, so that only decisions applied while this check runs make it stale.
                // A request made meanwhile is served by this check.
                entry.TrustCheckRequested = false;
                epoch = entry.TrustEpoch;
                MarkChanged(entry);
                Commit();
            }

            PluginTrustDecision? decision = null;
            Exception? failure = null;
            try
            {
                decision = _ops.CheckTrust(Root, entry.Id, cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
            }

            lock (_gate)
            {
                ApplyTrustCheck(entry, epoch, decision, failure);
                Commit();
            }

            // Dispatch as soon as a plugin is found approved.
            RequestPump();
        }
    }

    private void ApplyTrustCheck(PluginEntry entry, long epoch, PluginTrustDecision? decision, Exception? failure)
    {
        if (_lifecycle != SchedulerLifecycle.Running || !IsTracked(entry))
        {
            // Superseded by a removal or shutdown.
            return;
        }

        long now = Now();
        DateTimeOffset utc = _time.GetUtcNow();
        if (decision is null)
        {
            AddDiagnostic(new SchedulerDiagnostic(
                PluginDiagnosticSeverity.Warning,
                SchedulerDiagnosticCode.TrustCheckFailed,
                string.Create(CultureInfo.InvariantCulture, $"The trust check of {Describe(entry.Id)} failed ({failure!.GetType().Name}, HRESULT 0x{failure.HResult:X8}). The plugin is treated as unavailable."),
                entry.Id));
            ApplyObservation(entry, PluginTrustStatus.PluginUnavailable, approval: null, epoch, utc, now);
            return;
        }

        ApplyObservation(entry, decision.Status, decision.Approval, epoch, utc, now);
    }

    // Applies a discovery result. Only a complete result removes plugins; a plugin missing from an incomplete one is kept as
    // uncertain, with its identity, last descriptor and approval, and is suspended until its targeted trust check decides
    // whether its output is published again.
    private void ApplyDiscovery(PluginDiscoveryResult result)
    {
        long now = Now();
        DateTimeOffset utc = _time.GetUtcNow();
        _completedScans++;
        _lastScanUtc = utc;
        _lastScanComplete = result.IsComplete;
        _discoveryDiagnosticsTruncated = result.Diagnostics.Length > MaxDiscoveryDiagnostics;
        _discoveryDiagnostics = _discoveryDiagnosticsTruncated ? [.. result.Diagnostics.Take(MaxDiscoveryDiagnostics)] : result.Diagnostics;
        if (!_rootAvailable)
        {
            _rootAvailable = true;
            _rootProblemReported = false;
        }

        // Removals first, so that a complete scan at capacity makes room for the plugins it found.
        var seen = new HashSet<PluginId>(result.Plugins.Select(descriptor => descriptor.Id));
        foreach (PluginEntry entry in _plugins.Values.ToArray())
        {
            if (seen.Contains(entry.Id))
            {
                continue;
            }

            if (result.IsComplete)
            {
                Remove(entry);
            }
            else
            {
                MarkUncertain(entry);
            }
        }

        int untracked = 0;
        foreach (PluginDescriptor descriptor in result.Plugins)
        {
            if (_plugins.TryGetValue(descriptor.Id, out PluginEntry? entry))
            {
                Refresh(entry, descriptor);
                continue;
            }

            if (_plugins.Count >= _maxTracked)
            {
                // Overload: never evict a tracked plugin (or its active run or cleanup block) to make room.
                untracked++;
                continue;
            }

            // New: due at once for an interval plugin, but not launched before its trust check approves it.
            var added = new PluginEntry(descriptor) { TrustCheckRequested = descriptor.FileName.ExecutionSupport == PluginExecutionSupport.Supported };
            added.AwaitingTrustCheck = added.TrustCheckRequested;
            _plugins.Add(descriptor.Id, added);
            SetDue(added, now, immediate: true);
        }

        if (untracked > 0 && _untracked == 0)
        {
            AddDiagnostic(new SchedulerDiagnostic(
                PluginDiagnosticSeverity.Warning,
                SchedulerDiagnosticCode.TrackingCapacityReached,
                string.Create(CultureInfo.InvariantCulture, $"Discovery found {untracked} plugin(s) more than the {_maxTracked} the scheduler tracks. They are not scheduled.")));
        }

        _untracked = untracked;
        if (!result.IsComplete)
        {
            // Reported once per run of incomplete scans, not for every retry.
            if (!_incompleteReported)
            {
                _incompleteReported = true;
                AddDiagnostic(new SchedulerDiagnostic(
                    PluginDiagnosticSeverity.Warning,
                    SchedulerDiagnosticCode.DiscoveryIncomplete,
                    $"The scan of {TrustPaths.Quote(Root.Value)} was incomplete (see the discovery diagnostics). Plugins missing from it are kept as uncertain and are not launched; the scan is retried."));
            }

            _rescanRetryAt = now + _incompleteRetry;
        }
        else
        {
            _incompleteReported = false;
        }

        _dirty = true;
    }

    private void Refresh(PluginEntry entry, PluginDescriptor descriptor)
    {
        if (entry.Presence == PluginPresence.Uncertain)
        {
            // Back after uncertainty or root loss: nothing proves it was unchanged meanwhile, so it stays suspended until a
            // fresh trust check decides.
            entry.Presence = PluginPresence.Present;
            Suspend(entry, requestTrustCheck: true);
        }

        PluginDescriptor previous = entry.Descriptor;
        if (ReferenceEquals(previous, descriptor))
        {
            return;
        }

        bool sameContent = previous.Metadata == descriptor.Metadata
            && previous.Diagnostics.SequenceEqual(descriptor.Diagnostics)
            && string.Equals(previous.Id.Value, descriptor.Id.Value, StringComparison.OrdinalIgnoreCase);
        entry.Descriptor = descriptor;
        MarkChanged(entry);
        if (!sameContent)
        {
            // A different descriptor is a hint that the file may have changed, not proof: diagnostics also differ when a
            // read failed or was limited, and only the trust check's fingerprint comparison establishes a content change.
            // Conservatively, the output is withdrawn and an active run cancelled at once; the trust check then decides,
            // and invalidates the approval persistently only if the content did change. A case-only rename is not a change.
            Invalidate(entry, requestTrustCheck: entry.LanguageSupported);
        }
    }

    // Missing from an incomplete scan: not removal and not proof of a change. Kept with its identity and approval and not
    // launched (an active run continues, but its output is not published), and suspended: its output is withheld until
    // the targeted trust check requested here confirms the approval it was produced under.
    private void MarkUncertain(PluginEntry entry)
    {
        if (entry.Presence == PluginPresence.Uncertain)
        {
            // Later incomplete scans add no evidence, so a plugin already checked since it became uncertain is not checked
            // again (periodic reconciliation still checks it). One still suspended, for example after root loss, is.
            if (entry.AwaitingTrustCheck)
            {
                RequestTrustCheck(entry);
            }

            return;
        }

        entry.Presence = PluginPresence.Uncertain;
        entry.ManualPending = false;
        Suspend(entry, requestTrustCheck: true);
    }

    // Confirmed removal (absent from a complete scan). An active run is cancelled but keeps its slot and identity until it
    // ends; a cleanup block stays. Approvals are never touched.
    private void Remove(PluginEntry entry)
    {
        Invalidate(entry, requestTrustCheck: false);
        _plugins.Remove(entry.Id);
        _dirty = true;
    }

    // The root could not be validated or scanned: stronger evidence than a plugin missing from an incomplete listing, though
    // still neither removal nor proof of a change. Every plugin is suspended at once: its output is withheld, it is not
    // launched, and an active run is cancelled. Identities, descriptors and approvals are kept. No trust check is made while
    // the root is unavailable, because none could verify anything: a scan that lists the plugin again requests the fresh
    // check that decides whether it runs and whether its output is published again.
    private void ApplyRootUnavailable(SchedulerDiagnostic problem)
    {
        _lastScanComplete = false;
        _rootAvailable = false;
        if (!_rootProblemReported)
        {
            _rootProblemReported = true;
            AddDiagnostic(problem);
        }

        foreach (PluginEntry entry in _plugins.Values)
        {
            bool running = _active.TryGetValue(entry.Id, out Attempt? attempt) && ReferenceEquals(attempt.Entry, entry) && !attempt.CancelRequested;
            bool suspended = entry.Presence == PluginPresence.Uncertain && (entry.AwaitingTrustCheck || !entry.LanguageSupported);
            if (suspended && !running && !entry.TrustCheckRequested && !entry.ManualPending)
            {
                // Already suspended for the root by an earlier attempt to scan it: nothing to repeat.
                continue;
            }

            entry.Presence = PluginPresence.Uncertain;
            entry.ManualPending = false;
            entry.TrustCheckRequested = false;
            Supersede(entry);
            Suspend(entry, requestTrustCheck: false);
        }

        _rescanRetryAt = Now() + _incompleteRetry;
        _dirty = true;
    }

    // Watching -------------------------------------------------------------------------------------------------------------

    private IDisposable? TakeWatcher()
    {
        IDisposable? watcher = _watcher;
        _watcher = null;
        _watchedIdentity = null;
        _watcherGeneration++;
        _dirty = true;
        return watcher;
    }

    // Creates the watcher when there is none, when it failed, or when the root directory was replaced.
    private void EnsureWatcher(RootIdentity? identity)
    {
        IDisposable? stale = null;
        int generation;
        lock (_gate)
        {
            bool replaced = identity is not null && _watchedIdentity is not null && identity != _watchedIdentity;
            if (_lifecycle != SchedulerLifecycle.Running || (_watcher is not null && !_watcherNeedsRestart && !replaced))
            {
                return;
            }

            if (replaced)
            {
                AddDiagnostic(new SchedulerDiagnostic(
                    PluginDiagnosticSeverity.Info,
                    SchedulerDiagnosticCode.RootReplaced,
                    $"The plugin root {TrustPaths.Quote(Root.Value)} was replaced by another directory. Watching restarts; every plugin is suspended (output withheld, active runs cancelled) until a fresh trust check, made by this reconciliation, decides."));

                // Not a revocation: the trust check, made in this pass after the scan, applies the trust service's policy to
                // the files now at the root.
                foreach (PluginEntry entry in _plugins.Values)
                {
                    Supersede(entry);
                    Suspend(entry, requestTrustCheck: true);
                }
            }

            stale = TakeWatcher();
            _watcherNeedsRestart = false;
            generation = _watcherGeneration;
            Commit();
        }

        stale?.Dispose();
        IDisposable? created = _ops.CreateWatcher(Root, notification => OnWatcherNotification(generation, notification), out string? problem);
        lock (_gate)
        {
            if (_lifecycle == SchedulerLifecycle.Running && generation == _watcherGeneration)
            {
                _watcher = created;
                _watchedIdentity = identity;
                created = null;
                if (_watcher is null && !_watcherProblemReported)
                {
                    _watcherProblemReported = true;
                    AddDiagnostic(new SchedulerDiagnostic(
                        PluginDiagnosticSeverity.Warning,
                        SchedulerDiagnosticCode.WatcherUnavailable,
                        $"The plugin root {TrustPaths.Quote(Root.Value)} cannot be watched ({problem ?? "unknown reason"}). Changes are found by periodic reconciliation only; watching is retried."));
                }
                else if (_watcher is not null)
                {
                    _watcherProblemReported = false;
                }

                _dirty = true;
            }

            Commit();
        }

        // Superseded meanwhile (stopping, or a newer watcher): never keep two.
        created?.Dispose();
    }

    // Called on watcher threads: bounded work only. Nothing is scanned, hashed or launched here.
    private void OnWatcherNotification(int generation, WatcherNotification notification)
    {
        try
        {
            bool pump = false;
            lock (_gate)
            {
                if (generation != _watcherGeneration || _lifecycle != SchedulerLifecycle.Running)
                {
                    return;
                }

                switch (notification.Kind)
                {
                    case WatcherChangeKind.Overflow when _rescanRequested && _fullTrustRequested:
                        // A full reconciliation is already pending; repeated overflows during a storm add nothing.
                        break;

                    case WatcherChangeKind.Overflow:
                        AddDiagnostic(new SchedulerDiagnostic(
                            PluginDiagnosticSeverity.Warning,
                            SchedulerDiagnosticCode.WatcherOverflow,
                            "The file system watcher lost notifications. A full reconciliation follows."));
                        _rescanRequested = true;
                        _fullTrustRequested = true;
                        pump = true;
                        break;

                    case WatcherChangeKind.Failed:
                        AddDiagnostic(new SchedulerDiagnostic(
                            PluginDiagnosticSeverity.Warning,
                            SchedulerDiagnosticCode.WatcherFailed,
                            "The file system watcher failed. It is recreated, and a full reconciliation follows."));
                        _watcherNeedsRestart = true;
                        _rescanRequested = true;
                        _fullTrustRequested = true;
                        pump = true;
                        break;

                    default:
                        WatcherRelevance relevance = PluginRootWatcher.Classify(notification);
                        if (!relevance.Rescan)
                        {
                            return;
                        }

                        long now = Now();
                        if (!_watchDirty)
                        {
                            // Only the first notification of a burst wakes the pump; later ones just extend the debounce,
                            // within the maximum delay measured from the first.
                            _watchDirty = true;
                            _watchFirst = now;
                            pump = true;
                        }

                        _watchLast = now;
                        Hint(relevance.Plugin);
                        Hint(relevance.OldPlugin);
                        break;
                }

                Commit();
            }

            if (pump)
            {
                RequestPump();
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            FaultFromBackground(exception, "a file system notification");
        }
    }

    private void Hint(PluginId? id)
    {
        if (id is null || _hintsOverflowed)
        {
            return;
        }

        if (_hints.Count >= MaxWatcherHints)
        {
            // Bounded: too many distinct plugins changed, so every plugin is checked instead.
            _hints.Clear();
            _hintsOverflowed = true;
            return;
        }

        _hints.Add(id);
    }
}
