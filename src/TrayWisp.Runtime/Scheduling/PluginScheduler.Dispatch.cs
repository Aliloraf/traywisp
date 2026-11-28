using System.Collections.Immutable;
using System.Globalization;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using TrayWisp.Runtime.Execution;
using TrayWisp.Runtime.Trust;

namespace TrayWisp.Runtime.Scheduling;

// Dispatch, completion, trust observations and invalidation.
//
// Synchronization model: one lock (_gate) guards all mutable state. Code under it only changes memory: no file system
// access, no trust store operation, no process work, no consumer code, and no cancellation callbacks (a cancellation is
// signalled under the lock with CancelAsync, which marks the token cancelled at once and runs its callbacks on the thread
// pool). The one exception is disposing a cancellation source nothing can use any more: that runs no callbacks and only
// releases the source's event handle. The file system, trust and execution work runs on background tasks: at most one reconciliation pass at a time and
// at most MaxConcurrency attempts. Decisions are made by a coalesced pump (at most one queued or running at a time), woken
// by state changes and by a single timer.
public sealed partial class PluginScheduler
{
    // Requests a pump. Coalesced: at most one is queued or running, and one more is remembered while it runs.
    private void RequestPump()
    {
        while (true)
        {
            int state = Volatile.Read(ref _pumpState);
            if (state == 0)
            {
                if (Interlocked.CompareExchange(ref _pumpState, 1, 0) == 0)
                {
                    ThreadPool.UnsafeQueueUserWorkItem(static state => ((PluginScheduler)state!).RunPumps(), this);
                    return;
                }
            }
            else if (state == 1)
            {
                if (Interlocked.CompareExchange(ref _pumpState, 2, 1) == 1)
                {
                    return;
                }
            }
            else
            {
                return;
            }
        }
    }

    private void RunPumps()
    {
        try
        {
            while (true)
            {
                Volatile.Write(ref _pumpState, 1);
                try
                {
                    Pump();
                }
                catch (Exception exception) when (!IsFatal(exception))
                {
                    FaultFromBackground(exception, "the scheduling decision");
                }

                if (Interlocked.CompareExchange(ref _pumpState, 0, 1) == 1)
                {
                    return;
                }
            }
        }
        finally
        {
            Pulse();
        }
    }

    // One scheduling decision: start a reconciliation pass if one is due, dispatch ready plugins to free slots, and set the
    // timer to the next moment something becomes due.
    private void Pump()
    {
        lock (_gate)
        {
            if (_lifecycle == SchedulerLifecycle.Running)
            {
                long now = Now();
                StartReconciliationIfDue(now);
                Dispatch(now);
                ScheduleTimer(now);
            }

            Commit();
        }
    }

    // Dispatches ready plugins, oldest readiness first, while slots are free; marks the others queued.
    private void Dispatch(long now)
    {
        int available = _maxConcurrency - _active.Count - _blocks.Count;
        List<(PluginEntry Entry, long ReadySince)>? ready = null;
        foreach (PluginEntry entry in _plugins.Values)
        {
            if (!_executionHalted && IsReady(entry, now, out long readySince))
            {
                (ready ??= []).Add((entry, readySince));
            }
            else if (entry.Queued)
            {
                entry.Queued = false;
                MarkChanged(entry);
            }
        }

        if (ready is null)
        {
            return;
        }

        // Fairness: a plugin waits behind every plugin that became ready before it. One plugin can hold at most one slot
        // and has at most one pending manual refresh, and it becomes ready again only after its run has ended, so neither
        // a short interval nor repeated manual requests can starve the others.
        ready.Sort(static (left, right) =>
        {
            int order = left.ReadySince.CompareTo(right.ReadySince);
            return order != 0 ? order : left.Entry.Id.CompareTo(right.Entry.Id);
        });

        foreach ((PluginEntry entry, _) in ready)
        {
            bool spaced = entry.LastCompletedTimestamp is not long last || now - last >= _minimumRelaunch;
            if (available > 0 && spaced)
            {
                StartAttempt(entry, now);
                available--;
            }
            else if (!entry.Queued)
            {
                entry.Queued = true;
                MarkChanged(entry);
            }
        }
    }

    private bool IsReady(PluginEntry entry, long now, out long readySince)
    {
        readySince = 0;
        if (_active.ContainsKey(entry.Id) || Eligibility(entry) != PluginEligibility.Eligible || entry.Approval is null)
        {
            return false;
        }

        bool due = entry.DueTimestamp is long dueAt && dueAt <= now;
        if (!entry.ManualPending && !due)
        {
            return false;
        }

        readySince = entry.ManualPending && (!due || entry.ManualRequestedTimestamp < entry.DueTimestamp!.Value)
            ? entry.ManualRequestedTimestamp
            : entry.DueTimestamp!.Value;
        return true;
    }

    private void StartAttempt(PluginEntry entry, long now)
    {
        RefreshTrigger trigger = entry.ManualPending ? RefreshTrigger.Manual : RefreshTrigger.Automatic;
        var attempt = new Attempt(++_attemptSequence, entry, entry.Revision, entry.Approval!, trigger, now, _time.GetUtcNow());
        entry.ManualPending = false;
        entry.Queued = false;
        entry.DueTimestamp = null;
        entry.DueUtc = null;
        _active.Add(entry.Id, attempt);
        _undisposedAttemptSources++;
        MarkChanged(entry);

        // RunAttemptAsync yields at once, so nothing of the run executes under the lock.
        attempt.Task = RunAttemptAsync(attempt);
        ObserveFaults(attempt.Task, "a run", attempt);
    }

    private void ScheduleTimer(long now)
    {
        long wake = long.MaxValue;
        if (!_reconciling)
        {
            if (_watchDirty)
            {
                wake = Math.Min(wake, Math.Min(_watchLast + _debounce, _watchFirst + _maxWatcherDelay));
            }

            wake = Math.Min(wake, _nextFullAt);
            if (_rescanRetryAt is long retry)
            {
                wake = Math.Min(wake, retry);
            }
        }

        // Without a free slot the timer still wakes the pump once when a plugin becomes due, so that it is shown as queued;
        // a plugin already queued for a slot is woken by the completion that frees one, not by the timer.
        if (!_executionHalted)
        {
            foreach (PluginEntry entry in _plugins.Values)
            {
                if (_active.ContainsKey(entry.Id) || entry.Approval is null || Eligibility(entry) != PluginEligibility.Eligible)
                {
                    continue;
                }

                long? at = entry.ManualPending ? now : entry.DueTimestamp;
                if (at is not long candidate)
                {
                    continue;
                }

                if (entry.LastCompletedTimestamp is long last)
                {
                    candidate = Math.Max(candidate, last + _minimumRelaunch);
                }

                if (entry.Queued && candidate <= now)
                {
                    continue;
                }

                wake = Math.Min(wake, candidate);
            }
        }

        if (_timer is null)
        {
            return;
        }

        if (wake == long.MaxValue)
        {
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            return;
        }

        // Rounded up to whole milliseconds (the resolution of system timers), and at least 1 ms, so a timer that fires a
        // little early sets a new one instead of spinning.
        double milliseconds = Math.Ceiling(ToTimeSpan(wake - now).TotalMilliseconds);
        _timer.Change(TimeSpan.FromMilliseconds(Math.Max(milliseconds, 1)), Timeout.InfiniteTimeSpan);
    }

    // One attempt, from dispatch to completion. It keeps its slot until the engine has returned (after process cleanup)
    // and the output check has ended, whatever happens meanwhile.
    private async Task RunAttemptAsync(Attempt attempt)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        PluginExecutionResult? result = null;
        PluginTrustDecision? verification = null;
        Exception? fault = null;
        bool started = false;
        try
        {
            _hooks?.AttemptStarting?.Invoke(attempt.PluginId);

            // An attempt superseded since dispatch (disabled, removed, invalidated, suspended, stopping or faulted) never
            // reaches the engine. Its token was cancelled under the lock that superseded it, so an attempt superseded after
            // this decision reaches the engine with a token that is already cancelled, and the engine starts nothing unless
            // process creation is already under way (the window documented in execution-engine.md).
            lock (_gate)
            {
                started = IsCurrent(attempt) && !attempt.CancelRequested;
            }

            if (started)
            {
                _hooks?.EngineCalling?.Invoke(attempt.PluginId);
                Task<PluginExecutionResult> run = _ops.RunAsync(Root, attempt.PluginId, attempt.Expected, attempt.Cancellation.Token);
                attempt.RunTask = run;
                attempt.Phase = AttemptPhase.Running;
                Pulse();

                // The engine resumes some waits on timer threads; never continue there.
                result = await run.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                attempt.Phase = AttemptPhase.Finishing;
                if (ShouldVerify(attempt, result))
                {
                    // The output is published only if the approval that authorized the run is still exactly the current one.
                    verification = _ops.CheckApproval(result.Approval!, attempt.Cancellation.Token);
                }
            }
        }
        catch (OperationCanceledException) when (result is not null && attempt.Cancellation.IsCancellationRequested)
        {
            // The output check was cancelled: the output is not published.
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            fault = exception;
        }
        finally
        {
            attempt.Phase = AttemptPhase.Finishing;
        }

        Complete(attempt, result, verification, fault, started);
    }

    private bool ShouldVerify(Attempt attempt, PluginExecutionResult result)
    {
        if (!result.Succeeded || result.Output is null || result.Approval != attempt.Expected || result.PluginId != attempt.PluginId)
        {
            return false;
        }

        lock (_gate)
        {
            if (!IsCurrent(attempt))
            {
                return false;
            }

            // A denial from this check that started before another trust observation of the plugin is stale.
            attempt.VerifyEpoch = attempt.Entry.TrustEpoch;
            return true;
        }
    }

    private bool IsCurrent(Attempt attempt) =>
        _lifecycle == SchedulerLifecycle.Running && IsTracked(attempt.Entry) && attempt.Entry.Revision == attempt.Revision;

    private void Complete(Attempt attempt, PluginExecutionResult? result, PluginTrustDecision? verification, Exception? fault, bool started)
    {
        // Without confirmed cleanup, processes may still run. An attempt that never reached the engine started nothing.
        bool cleanupUnresolved = started && (result is null || PluginSchedulingPolicy.IsCleanupUnresolved(result.Status, result.Cleanup.Status));
        try
        {
            lock (_gate)
            {
                // The engine call and the output check have returned: the worker never uses the token again.
                attempt.WorkerEnded = true;
                _hooks?.FaultPoint?.Invoke(SchedulerFaultPoint.Completion);
                long now = Now();
                DateTimeOffset utc = _time.GetUtcNow();

                // Recorded first, whatever became of the plugin or the attempt, so that no later failure can lose it.
                if (cleanupUnresolved)
                {
                    RecordCleanupIncident(attempt, utc);
                }

                if (_active.TryGetValue(attempt.PluginId, out Attempt? active) && ReferenceEquals(active, attempt))
                {
                    _active.Remove(attempt.PluginId);
                }

                var notes = new List<SchedulerDiagnostic>();
                if (fault is not null)
                {
                    Fault(fault, result is null ? "the execution engine" : "the output trust check");
                }

                PluginEntry? entry = _plugins.GetValueOrDefault(attempt.PluginId);
                if (entry is not null)
                {
                    // The minimum relaunch delay applies to the identity, also for a reappeared entry.
                    entry.LastCompletedTimestamp = now;
                }

                if (!started || !IsCurrent(attempt))
                {
                    // Superseded: an invalidation, suspension, removal, disabling, shutdown or fault happened since dispatch
                    // (before the engine was called, when it was not started). Nothing of this result is applied: no
                    // output, no trust observation, no schedule decision based on it.
                    if (result?.Succeeded == true)
                    {
                        notes.Add(NotPublished(attempt, _lifecycle == SchedulerLifecycle.Running ? "the run was superseded by a later change" : "the scheduler is stopping"));
                    }

                    if (entry is not null && ReferenceEquals(entry, attempt.Entry))
                    {
                        entry.LastRun = Summary(attempt, result, result is null && started ? PluginRunDisposition.Faulted : PluginRunDisposition.Superseded, started, now, utc, notes);
                        if (entry.DueTimestamp is null && entry.Enabled)
                        {
                            SetDue(entry, now, immediate: false);
                        }

                        MarkChanged(entry);
                    }
                }
                else
                {
                    CompleteCurrent(attempt, attempt.Entry, result, verification, now, utc, notes);
                }

                // Unregistered above, so nothing can signal it any more; released now unless a delivery still runs.
                TryReleaseCancellation(attempt);
                _dirty = true;
                TryFinishStop();
                Commit();

                // Requested before the lock is released: the freed slot is never observable without its follow-up decision.
                RequestPump();
            }
        }
        catch (Exception exception) when (!IsFatal(exception))
        {
            CompletionFailed(attempt, cleanupUnresolved, exception);
        }

        Pulse();
    }

    // A completion failed part-way: a programming error. The scheduler fails closed, and the run is never lost: an
    // unresolved cleanup is recorded if it is not yet, and if even that is impossible, the attempt stays registered, keeps
    // its slot and is reported as outstanding by StopAsync, and keeps its cancellation source. A failure here is observed
    // by ObserveFaults.
    private void CompletionFailed(Attempt attempt, bool cleanupUnresolved, Exception exception)
    {
        lock (_gate)
        {
            attempt.WorkerEnded = true;
            _hooks?.FaultPoint?.Invoke(SchedulerFaultPoint.CompletionRecovery);
            Fault(exception, "the completion of a run");
            if (cleanupUnresolved && !_blocks.ContainsKey(attempt.PluginId))
            {
                RecordCleanupIncident(attempt, _time.GetUtcNow());
            }

            bool accounted = !cleanupUnresolved || _blocks.ContainsKey(attempt.PluginId);
            if (accounted && _active.TryGetValue(attempt.PluginId, out Attempt? active) && ReferenceEquals(active, attempt))
            {
                _active.Remove(attempt.PluginId);
            }

            TryReleaseCancellation(attempt);
            _dirty = true;
            TryFinishStop();
            Commit();
        }
    }

    private void CompleteCurrent(Attempt attempt, PluginEntry entry, PluginExecutionResult? result, PluginTrustDecision? verification, long now, DateTimeOffset utc, List<SchedulerDiagnostic> notes)
    {
        PluginRunDisposition disposition;
        if (result is null)
        {
            disposition = PluginRunDisposition.Faulted;
            ClearOutput(entry);
        }
        else if (result.Succeeded && result.Output is not null)
        {
            disposition = TryPublish(attempt, entry, result, verification, utc, notes) ? PluginRunDisposition.Published : PluginRunDisposition.NotPublished;
        }
        else
        {
            // Conservative: a failed run withdraws earlier output instead of leaving stale menus.
            disposition = PluginRunDisposition.Failed;
            ClearOutput(entry);
            if (result.Status == PluginExecutionStatus.TrustDenied && result.TrustStatus is { } denied)
            {
                // The launch-time check started after dispatch.
                ApplyObservation(entry, denied, approval: null, attempt.LaunchEpoch, utc, now);
            }
        }

        bool succeeded = disposition == PluginRunDisposition.Published;
        entry.ConsecutiveFailures = succeeded ? 0 : entry.ConsecutiveFailures + 1;
        TimeSpan? delay = PluginSchedulingPolicy.GetNextAutomaticDelay(entry.Interval, succeeded, entry.ConsecutiveFailures);
        SetDue(entry, now, immediate: false, clear: delay is null || !entry.Enabled, delay);
        entry.LastRun = Summary(attempt, result, disposition, started: true, now, utc, notes);
        MarkChanged(entry);
    }

    private bool TryPublish(Attempt attempt, PluginEntry entry, PluginExecutionResult result, PluginTrustDecision? verification, DateTimeOffset utc, List<SchedulerDiagnostic> notes)
    {
        PluginEligibility eligibility = Eligibility(entry);
        if (eligibility != PluginEligibility.Eligible)
        {
            // For example uncertain after an incomplete scan during the run: publication is suspended, not decided later.
            ClearOutput(entry);
            notes.Add(NotPublished(attempt, $"the plugin is not eligible ({eligibility})"));
            return false;
        }

        bool verified = verification is { IsApproved: true }
            && verification.Approval == attempt.Expected
            && result.Approval == attempt.Expected
            && entry.Approval == attempt.Expected;
        if (!verified)
        {
            ClearOutput(entry);
            notes.Add(NotPublished(attempt, verification is null ? "its trust could not be verified" : $"the trust check of its approval returned {verification.Status}"));
            if (verification is not null)
            {
                ApplyObservation(entry, verification.Status, verification.Approval, attempt.VerifyEpoch, utc, Now());
            }
            else
            {
                RequestTrustCheck(entry);
            }

            return false;
        }

        long cost = PluginSchedulingPolicy.EstimateRetainedOutputCost(result.Output!);
        if (_retainedOutputCost - entry.OutputCost + cost > _maxOutputCost)
        {
            ClearOutput(entry);
            notes.Add(new SchedulerDiagnostic(
                PluginDiagnosticSeverity.Warning,
                SchedulerDiagnosticCode.OutputBudgetExceeded,
                string.Create(CultureInfo.InvariantCulture, $"The output of {Describe(entry.Id)} was not published: the budget of {_maxOutputCost} bytes for retained output is exhausted."),
                entry.Id));
            AddDiagnostic(notes[^1]);
            return false;
        }

        ClearOutput(entry);
        entry.Output = new PublishedPluginOutput(result.Output!, attempt.Expected, attempt.Id, utc);
        entry.OutputCost = cost;
        _retainedOutputCost += cost;

        // The output check is an applied trust observation: a reconciliation check that started before it is stale.
        entry.TrustEpoch++;
        entry.TrustObservedAtUtc = utc;
        return true;
    }

    private void RecordCleanupIncident(Attempt attempt, DateTimeOffset utc)
    {
        if (_blocks.ContainsKey(attempt.PluginId))
        {
            return;
        }

        if (_blocks.Count >= _maxCleanupIncidents)
        {
            // Never forget an incident to make room: launch nothing more instead.
            _executionHalted = true;
            MarkAllChanged();
            AddDiagnostic(new SchedulerDiagnostic(
                PluginDiagnosticSeverity.Error,
                SchedulerDiagnosticCode.CleanupRegistryFull,
                $"The cleanup of a run of {Describe(attempt.PluginId)} could not be confirmed and no further incident can be recorded. The scheduler launches nothing more.",
                attempt.PluginId));
            return;
        }

        _blocks.Add(attempt.PluginId, new PluginCleanupBlock(attempt.PluginId, attempt.Id, utc));
        AddDiagnostic(new SchedulerDiagnostic(
            PluginDiagnosticSeverity.Error,
            SchedulerDiagnosticCode.CleanupIncomplete,
            $"The process cleanup of run {attempt.Id.ToString(CultureInfo.InvariantCulture)} of {Describe(attempt.PluginId)} could not be confirmed; processes may still be running. "
            + "This scheduler never launches the plugin again and keeps one execution slot reserved. Verify and end those processes yourself; restarting TrayWisp does not prove they ended.",
            attempt.PluginId));
        if (_plugins.TryGetValue(attempt.PluginId, out PluginEntry? entry))
        {
            entry.ManualPending = false;
            ClearOutput(entry);
            MarkChanged(entry);
        }
    }

    // Applies a trust observation made by a reconciliation check, the launch-time check or an output check. startedEpoch is
    // the entry's trust epoch when that check started. Every applied observation, invalidation and suspension advances the
    // epoch, so a check that started earlier is stale: another decision was applied meanwhile, and the two checks' order
    // is unknown. A stale observation can only take away, never overwrite a newer decision: a stale approval is discarded,
    // and a stale denial withdraws whatever approval the entry holds. Either way, a fresh check decides when no current
    // decision would otherwise remain. Applying it never touches the trust store: persistent invalidation and deny-only
    // unsaved invalidations are the trust service's, made during the check.
    private void ApplyObservation(PluginEntry entry, PluginTrustStatus status, PluginApprovalIdentity? approval, long startedEpoch, DateTimeOffset utc, long now)
    {
        if (entry.TrustEpoch == startedEpoch)
        {
            entry.AwaitingTrustCheck = false;
            ObserveTrust(entry, status, approval, utc, now);
            return;
        }

        if (status != PluginTrustStatus.Approved && (entry.Approval is not null || entry.Output is not null))
        {
            // As a trust-change notification: output withdrawn, an active run cancelled, nothing launched before a check.
            Invalidate(entry, requestTrustCheck: entry.LanguageSupported);
        }
        else if (entry.AwaitingTrustCheck || (status == PluginTrustStatus.Approved && entry.Approval is null))
        {
            // Either no decision is current, or the discarded approval may be the newer observation: check again. One
            // extra check per race, never a loop: the new check is fresh unless yet another decision intervenes.
            RequestTrustCheck(entry);
        }
    }

    // Applies a current trust observation. Losing an approval, or an approval that changed, invalidates the plugin's state:
    // output is withdrawn and an active run cancelled. A new approval makes an interval plugin due at once and resets its
    // backoff. Confirming the approval its output was produced under ends a suspension of that output.
    private void ObserveTrust(PluginEntry entry, PluginTrustStatus status, PluginApprovalIdentity? approval, DateTimeOffset utc, long now)
    {
        entry.TrustEpoch++;
        PluginApprovalIdentity? previous = entry.Approval;
        PluginApprovalIdentity? current = status == PluginTrustStatus.Approved ? approval : null;
        entry.Trust = status;
        entry.Approval = current;
        entry.TrustObservedAtUtc = utc;
        if (previous is not null && previous != current)
        {
            // A stale approval means another approval replaced it: find out which.
            Invalidate(entry, requestTrustCheck: status == PluginTrustStatus.StaleApproval);
        }
        else if (current is null)
        {
            ClearOutput(entry);
        }

        if (current is not null && previous != current)
        {
            entry.ConsecutiveFailures = 0;
            SetDue(entry, now, immediate: true);
        }

        if (current is null)
        {
            // A manual request accepted while the check was pending is dropped when the check denies.
            entry.ManualPending = false;
        }

        MarkChanged(entry);
    }

    private PluginCommandStatus InvalidateForTrustChange(PluginEntry entry)
    {
        Invalidate(entry, requestTrustCheck: entry.LanguageSupported);
        return PluginCommandStatus.Applied;
    }

    // Marks the entry's current lifecycle as superseded and its trust state as changed: output withdrawn, an active run
    // cancelled (it keeps its slot until it ends, and its late completion is ignored), checks that started earlier stale,
    // and, if requested, nothing launched before a fresh trust check.
    private void Invalidate(PluginEntry entry, bool requestTrustCheck)
    {
        Supersede(entry);
        ClearOutput(entry);
        entry.TrustEpoch++;
        if (requestTrustCheck)
        {
            entry.AwaitingTrustCheck = true;
            entry.TrustCheckRequested = true;
        }

        MarkChanged(entry);
    }

    // A later completion of the entry's active run is ignored, and that run is cancelled at once (it keeps its slot until
    // the engine returns).
    private void Supersede(PluginEntry entry)
    {
        entry.Revision++;
        if (_active.TryGetValue(entry.Id, out Attempt? attempt) && ReferenceEquals(attempt.Entry, entry))
        {
            RequestCancel(attempt);
        }

        entry.Queued = false;
        MarkChanged(entry);
    }

    // Suspends the entry until a fresh trust check has been applied: nothing is launched, its output is withheld (kept and
    // counted, but not published), and checks that started earlier are stale. Its identity, descriptor, approval and
    // output are kept: the check decides whether the output is published again or withdrawn.
    private void Suspend(PluginEntry entry, bool requestTrustCheck)
    {
        entry.TrustEpoch++;
        entry.AwaitingTrustCheck = entry.LanguageSupported;
        entry.Queued = false;
        if (requestTrustCheck)
        {
            RequestTrustCheck(entry);
        }

        MarkChanged(entry);
    }

    // Output is published only while no trust decision is pending and the latest applied observation approves exactly the
    // approval the output was produced under.
    private static bool IsPublishable(PluginEntry entry) =>
        entry.Output is { } output && !entry.AwaitingTrustCheck && entry.Trust == PluginTrustStatus.Approved && entry.Approval == output.Approval;

    private void RequestTrustCheck(PluginEntry entry)
    {
        if (entry.LanguageSupported && !entry.TrustCheckRequested)
        {
            entry.TrustCheckRequested = true;
            MarkChanged(entry);
        }
    }

    private void ClearOutput(PluginEntry entry)
    {
        if (entry.Output is null)
        {
            return;
        }

        _retainedOutputCost -= entry.OutputCost;
        entry.Output = null;
        entry.OutputCost = 0;
        MarkChanged(entry);
    }

    private PluginRunSummary Summary(Attempt attempt, PluginExecutionResult? result, PluginRunDisposition disposition, bool started, long now, DateTimeOffset utc, List<SchedulerDiagnostic> notes)
    {
        bool truncated = result is not null
            && (result.Diagnostics.Length > MaxRunDiagnostics || result.TrustDiagnostics.Length > MaxRunDiagnostics || result.OutputDiagnostics.Length > MaxRunDiagnostics);
        foreach (SchedulerDiagnostic note in notes)
        {
            if (note.Code == SchedulerDiagnosticCode.OutputNotPublished)
            {
                AddDiagnostic(note);
            }
        }

        return new PluginRunSummary(
            attempt.Id,
            attempt.Trigger,
            attempt.StartedAtUtc,
            utc,
            ToTimeSpan(now - attempt.StartTimestamp),
            disposition,
            result?.Status,
            result?.TrustStatus,
            result?.ExitCode,
            result?.Cleanup.Status ?? (started ? ProcessCleanupStatus.Incomplete : ProcessCleanupStatus.NotStarted),
            result is null ? [] : Bounded(result.Diagnostics),
            result is null ? [] : Bounded(result.TrustDiagnostics),
            result is null ? [] : Bounded(result.OutputDiagnostics),
            truncated,
            [.. notes]);
    }

    private static ImmutableArray<T> Bounded<T>(ImmutableArray<T> items) => items.Length <= MaxRunDiagnostics ? items : [.. items.Take(MaxRunDiagnostics)];

    private static SchedulerDiagnostic NotPublished(Attempt attempt, string reason) =>
        new(
            PluginDiagnosticSeverity.Warning,
            SchedulerDiagnosticCode.OutputNotPublished,
            $"The output of run {attempt.Id.ToString(CultureInfo.InvariantCulture)} of {Describe(attempt.PluginId)} was not published: {reason}.",
            attempt.PluginId);

    private static string Describe(PluginId id) => TrustPaths.Quote(id.Value);
}
