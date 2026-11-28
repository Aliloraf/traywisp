# Scheduling and hot reload

This document specifies how TrayWisp keeps the plugins of one plugin root discovered, trust-checked and refreshed: initial discovery, interval and manual refreshes, file system watching, reconciliation, cancellation after relevant changes, the concurrency limit, publication of output, incomplete-cleanup protection and shutdown.

**Status (Phase 5).** Everything in this document is **implemented and tested** as a library in `TrayWisp.Runtime/Scheduling` and `TrayWisp.Core/Scheduling`, except where a section says *planned*. **No host uses the scheduler yet**: the CLI and the desktop application are unchanged, and there is no tray icon or status bar. Phases 6 to 9 will consume it as described in [Requirements for future consumers](#requirements-for-future-cli-and-ui-consumers).

**Approval is consent, not sandboxing.** The scheduler only decides *when* the execution engine is asked to run an approved plugin. A plugin that runs has every right of your Windows account. See [security.md](security.md#trust-model).

Related specifications: [plugin-format.md](plugin-format.md) (discovery), [trust-model.md](trust-model.md) (approval and the trust check), [execution-engine.md](execution-engine.md) (runs), [output-protocol.md](output-protocol.md) (output).

## Overview

```text
Host: one PluginTrustService ─► one PluginExecutionService ─► one PluginScheduler per plugin root

PluginScheduler (Runtime)
  ├─ coordinator: one lock over in-memory state; a coalesced pump; one timer; immutable snapshots
  ├─ reconciliation pass (at most one at a time, background):
  │    root validation (drive type → directory chain → short-name check) → watcher → PluginDiscovery.Discover
  │    → apply (complete: add/update/remove; incomplete: keep as uncertain) → trust checks (CheckTrust) → apply
  ├─ FileSystemWatcher (recursive): notifications are hints → debounced rescan + targeted trust checks
  ├─ attempts (at most MaxConcurrency, background):
  │    RunAsync(root, id, expected approval) ─ launch-time CheckTrust + lease, unchanged ─ process, cleanup
  │    → CheckTrust(result.Approval) → atomic commit (still current?) → publish or reject
  └─ Core: PluginSchedulingPolicy (delays, backoff, eligibility, status, output cost), diagnostics TW700–TW799
```

Nothing the scheduler remembers authorizes anything. Every launch is authorized by the execution engine's own authoritative check made immediately before it, and every publication by a fresh check of the producing approval.

## Public API

Runtime (`TrayWisp.Runtime.Scheduling`):

| Member | Role |
| --- | --- |
| `PluginScheduler(executionService, rootPath, options)` | One scheduler for one plugin root. Takes the host's `PluginExecutionService` and makes **every** trust check with that service's own `PluginTrustService`, so scheduling and execution can never consult different trust stores or service instances. Checks only the root's syntax (`PluginRoot` rules), that it does not overlap the trust store directory, and the options. No file system access. |
| `Root` | The normalized `PluginRoot`. |
| `Snapshot` | The latest immutable `PluginSchedulerSnapshot`. |
| `WaitForChangeAsync(afterRevision, token)` | Completes with the first snapshot newer than `afterRevision`, at once when the current one is. `afterRevision` must be the revision of a snapshot of this scheduler: a value larger than the current revision throws `ArgumentOutOfRangeException` (synchronously), because the next snapshot would not be newer than it. After the scheduler has stopped no newer snapshot follows, so waiters should pass a token. |
| `StartAsync(token)` | Starts discovery, watching and scheduling; completes when the initial reconciliation has been applied. |
| `StopAsync(token)` → `SchedulerStopResult` | Bounded shutdown; concurrent and repeated calls share one shutdown; reports what is still outstanding. |
| `DisposeAsync()` | `StopAsync()`. |
| `RequestRefresh(pluginId)` → `RefreshRequestStatus` | One manual refresh: accepted, coalesced or rejected with a reason. |
| `RequestRescan()` → `bool` | A discovery scan as soon as no reconciliation runs. **Discovery only:** it finds added, removed and renamed plugins and changed descriptors, not an edit that leaves the metadata unchanged. |
| `RequestReconciliation()` → `bool` | A full reconciliation as soon as no reconciliation runs: a scan, then an authoritative trust check of every supported plugin, as the periodic one. The way to revalidate at once, for example after editing a plugin outside a watched folder. Unlike `NotifyTrustChanged()`, it withdraws output and cancels runs only for plugins whose check does not confirm their approval. |
| `NotifyTrustChanged(pluginId)`, `NotifyTrustChanged()` → `PluginCommandStatus` | The host changed approvals (see [Trust integration](#trust-approval-and-revocation)). |
| `SetEnabled(pluginId, enabled)` → `PluginCommandStatus` | In-memory enabling and disabling, for this instance. |
| `PluginSchedulerOptions` | `MaxConcurrency`, `WatcherDebounce`, `WatcherMaxDelay`, `ReconciliationInterval`, `ShutdownTimeout`, `RunOptions` (see [Limits](#limits-and-defaults)). |
| `PluginSchedulerSnapshot`, `ScheduledPlugin`, `PluginRunSummary`, `PublishedPluginOutput`, `SchedulerDiscoveryState`, `PluginCleanupBlock`, `SchedulerStopResult` | Immutable results with internal constructors. |

Core (`TrayWisp.Core.Scheduling`), pure: `SchedulerLifecycle`, `PluginPresence`, `PluginActivity`, `PluginEligibility`, `PluginScheduleStatus`, `RefreshTrigger`, `RefreshRequestStatus`, `PluginCommandStatus`, `PluginRunDisposition`, `PluginEligibilityFacts`, `PluginSchedulingPolicy` and `SchedulerDiagnostic`/`SchedulerDiagnosticCode`. Core reads no clock: Runtime measures time and applies the policy (`CoreIsolationTests`).

Internal, not reachable from outside Runtime: the file system, trust and execution seam (`SchedulerOperations`) and its production implementation, the watcher, the coordinator's state, test hooks, and the execution engine's expected-approval overload. There is **no public test seam, no arbitrary process runner, no delegate that could replace the trust check, and no unchecked launch path** (`SchedulerApiTests`).

The host owns one long-lived `PluginTrustService`, one `PluginExecutionService` built on it, and one scheduler for its configured root:

```csharp
var trust = PluginTrustService.CreateForCurrentUser();
var execution = new PluginExecutionService(trust, executionOptions);
await using var scheduler = new PluginScheduler(execution, pluginRoot);
await scheduler.StartAsync(token);
```

## Lifecycle

`NotStarted` → `Running` → `Stopping` → `Stopped`, with `Faulted` reachable from `Running`. An instance is **single-use**: it is started once and stopped once.

- `StartAsync` moves to `Running`, creates the single timer and queues the initial reconciliation. Calling it again while running (or faulted) creates nothing (no second worker, timer or watcher) and waits for the same initial reconciliation. Its token cancels only the wait. After `StopAsync` it throws `InvalidOperationException`; create a new instance instead.
- `StopAsync` before `StartAsync` moves straight to `Stopped`.
- `Stopped` is reached only when nothing the scheduler started or owns is outstanding: no run, no reconciliation, no watcher or timer disposal and no cancellation delivery.
- `Faulted` follows an unexpected internal failure (see [Faults](#faults)): nothing more is launched or published; the host must still call `StopAsync` to wait for outstanding work.

## State model and snapshots

A `PluginSchedulerSnapshot` holds the lifecycle, a monotonically increasing `Revision`, the root, the tracked plugins (ordered by identity), discovery and watcher state, cleanup blocks, the concurrency limit, active runs, retired runs, whether execution is halted, the retained-output cost and the most recent scheduler diagnostics.

Each `ScheduledPlugin` separates **independent dimensions** instead of one large state enumeration:

| Dimension | Values |
| --- | --- |
| `Eligibility` | `Eligible`, or the most important reason why not, in this precedence: `CleanupBlocked`, `UnsupportedLanguage`, `Disabled`, `Uncertain`, `AwaitingTrustCheck`, `PendingApproval` (trust `PendingApproval`, `ContentChanged`, `StaleApproval`), `Unavailable` (trust `PluginUnavailable`, `UnsafePath`, `StoreUnavailable`). |
| `Activity` | `None`, `Queued` (due and waiting for a slot or the minimum relaunch delay), `Running`, `Cancelling` (cancellation requested; the run still holds its slot). |
| `Presence` | `Present`, or `Uncertain` (missing from an incomplete scan, or the root could not be scanned). |
| `IsEnabled`, `ObservedTrustStatus`, `TrustObservedAtUtc`, `TrustCheckPending` | Host and trust facts. `TrustCheckPending` is true while a fresh check is requested or required; the plugin is then not launched and its output is withheld. |
| `ManualRefreshPending`, `NextRefreshUtc`, `ConsecutiveFailures` | Schedule facts. `NextRefreshUtc` only for an eligible plugin, and only while the scheduler can still launch. |
| `LastRun` | A bounded `PluginRunSummary` of the latest completed run only. |
| `Output` | The current publishable output, or `null` (see [Output publication](#output-publication) for when retained output is withheld). |
| `LifecycleRevision` | Increases with every invalidation, and with every suspension that cancels runs (root unavailable or replaced). |

`Status` (`PluginScheduleStatus`) is derived from them by `PluginSchedulingPolicy.GetStatus`: a reason that prevents launching always wins (`CleanupBlocked`, `UnsupportedLanguage`, `Disabled`, `Uncertain`, `AwaitingTrustCheck`, `PendingApproval`, `Unavailable`); otherwise `Running`, `Queued`, `Failed` (the last run failed or was not published) or `Idle`. Because it is derived, the dimensions cannot contradict it.

**A cached `Approved` is informational.** `ObservedTrustStatus` decides eligibility (whether the scheduler asks the engine at all), never authorization.

**Snapshots are immutable.** A snapshot a consumer already holds never changes, including its output. Consumers must follow revisions and treat every snapshot as a description of a past moment. A newer snapshot is published only when something changed: time passing and irrelevant notifications publish nothing.

**No implied future work.** Once the scheduler launches nothing more (`Stopping`, `Stopped`, `Faulted`, or `ExecutionHalted`), no plugin is shown as `Queued`, with a `NextRefreshUtc` or with a pending manual refresh, and `Discovery.NextReconciliationUtc` is `null`.

**Bounded run information.** `PluginRunSummary` keeps the run identifier, trigger, timestamps, duration, disposition, execution and trust status, exit code, cleanup status, at most 8 execution, 8 trust and 8 output diagnostics (`DiagnosticsTruncated` otherwise) and the scheduler's own notes. It holds **no output, no standard output or error, and no approval identity**, so withdrawn output can never be reached through it. Execution history is not kept.

## Scheduling policy

- **Interval plugins** (`clock.5s.ps1`) use the `RefreshInterval` from the file name (250 ms to 30 days). An approved, enabled interval plugin is due **at once** when it is first discovered, newly approved (a new approval identity), or enabled.
- **Manual-only plugins** (`hello.ps1`, no interval) run only after `RequestRefresh`. They never get automatic runs or automatic retries.
- **Fixed delay.** The next automatic run is due one interval after the previous attempt **ended**, including process cleanup and the output check (`PluginSchedulingPolicy.GetNextAutomaticDelay`).
- **No catch-up.** Missed intervals (sleep, suspension, a long run, a busy queue) are never made up: at most one run follows.
- **Monotonic time.** Decisions use `TimeProvider.GetTimestamp`; wall-clock changes do not move schedules (tested with clock jumps). UTC values in snapshots are for display.
- **Failure backoff.** After a run that failed or whose output was not published, the next automatic run waits the longer of the interval and the backoff: 5 s, 10 s, 20 s, 40 s, then 60 s for every further consecutive failure. A plugin is therefore never retried faster than its interval, and a 2-minute plugin still waits 2 minutes. The count resets after a published success, a new approval, or enabling.
- **Minimum relaunch delay.** At least 250 ms (`PluginSchedulingPolicy.MinimumRelaunchDelay`) separate the end of one run of a plugin identity from the start of the next, for automatic and manual runs alike. This prevents rapid loops even for a plugin that fails at once and is refreshed manually without pause.
- **Unapproved, disabled, uncertain, unavailable, unsupported or blocked plugins are never dispatched.** Unsupported languages (Bash, batch) are tracked and shown, never trust-checked and never launched.

## Manual refresh

`RequestRefresh` uses exactly the same eligibility, authorization, concurrency, relaunch-delay and execution path as automatic refreshes.

| Result | Meaning |
| --- | --- |
| `Accepted` | A refresh is now pending. Also returned while the plugin awaits its trust check: the refresh runs only if that check approves, and is dropped otherwise. |
| `Coalesced` | One was already pending. At most one pending refresh exists per plugin; requests never queue. |
| `NotRunning`, `ExecutionHalted` | The scheduler is not running, or launches nothing more. |
| `UnknownPlugin`, `Disabled`, `UnsupportedLanguage`, `CleanupBlocked`, `Uncertain`, `NotApproved`, `Unavailable` | Rejected; nothing is pending. |

A request during an active run becomes one follow-up run after it (and after the minimum relaunch delay). When a manual run of an interval plugin completes, its periodic schedule restarts from that completion.

## Concurrency and fairness

- **Global limit.** At most `MaxConcurrency` attempts (default 4, 1–16) are in progress. An attempt occupies its slot from dispatch through interpreter resolution, the launch-time trust check, the process, its cleanup and the scheduler's output trust check. Requesting cancellation does **not** free the slot; the engine's return does.
- **Cleanup incidents.** Each unresolved cleanup incident permanently occupies one slot as well (see [Incomplete cleanup](#incomplete-cleanup-protection)).
- **One attempt per plugin identity.** Active attempts are tracked by `PluginId` (ordinal case-insensitive, within the scheduler's single root, so equivalent to `PluginTrustKey`), separately from the tracked plugin state. A cancelled, disabled, removed, renamed or modified plugin's attempt keeps its slot and its identity until it ends: a reappearing plugin with the same identity is not launched before that (`RetiredRuns` in the snapshot).
- **Fairness.** Ready plugins are served oldest readiness first (the earlier of its due time and its manual request), ties by identity. One plugin holds at most one slot and has at most one pending manual refresh, and becomes ready again only after its run has ended, so neither a 250 ms interval nor repeated manual requests can starve other plugins (tested with one slot and four always-ready plugins).
- **Bounded storage.** There is no ready queue: readiness is computed from the tracked plugins (at most 500) at each decision.
- **Scope.** These guarantees hold within **one scheduler instance**. Two schedulers, or two processes, for the same root do not coordinate; a host must create exactly one scheduler per root.

### Synchronization model

- One lock guards all mutable state. Code under it only changes memory and publishes snapshots: no file system access, no trust store operation, no process work, no consumer code and no cancellation callbacks (disposing a cancellation source that nothing can use any more is the one exception: it runs no callbacks and only releases the source's event handle). Cancellation is requested under the lock with `CancellationTokenSource.CancelAsync`, which marks the token cancelled before it returns and runs its callbacks (the engine's) on the thread pool. So the call that supersedes a run has signalled its token when it returns, and no callback runs under the lock. A delivery still running is tracked: shutdown waits for it, and a callback failure faults the scheduler (`TW700`).
- Decisions are made by a **pump**: at most one is queued or running, and one more is remembered while it runs. State changes, completions and the single timer request it. The timer is set to the next moment something becomes due, rounded up to whole milliseconds and at least 1 ms, so it never spins.
- File system and trust work runs in **one reconciliation pass at a time**; runs in **at most `MaxConcurrency` attempt tasks**. There is no task per plugin, no task per notification, no unbounded channel and no polling loop.
- Watcher callbacks only classify the notification and record a few fields under the lock.
- Every follow-up decision that a completion makes necessary is requested before the completion releases the lock, so a freed slot is never observable without its follow-up decision.

## Discovery and reconciliation

A **reconciliation pass** runs when: the scheduler starts; a rescan is requested; watcher notifications have settled; the periodic interval (default 30 s, from the end of the previous periodic pass) elapsed; an incomplete scan or unavailable root is retried (5 s); or trust checks were requested. It:

1. **Validates the root** exactly as the trust checks do, in an order that never touches what is not yet validated: the drive type first (`GetDriveType`, local fixed, removable or RAM drives only), then every directory from the drive root down, opened without following links (no reparse point, not offline or remote, no case-sensitive ancestor, final path equal to the root), then the short-name check (`Path.GetFullPath` must leave the path unchanged). The short-name expansion therefore never runs on an unvalidated path. The root stays open, without delete sharing, for the scan.
2. Starts or restarts the **watcher** (before the scan, so changes during the scan are seen).
3. Runs the unchanged **`PluginDiscovery.Discover`** with its default limits and diagnostics.
4. **Applies** the result.
5. Makes the requested **trust checks**, one plugin at a time, applying each result as it arrives (so dispatch starts as soon as a plugin is found approved).

Passes never overlap, so a scan can never be applied after a newer one. Trust observations are ordered as described in [Ordering of trust observations](#ordering-of-trust-observations). While the root is unavailable, a pass makes no trust check.

### Applying a scan

| Observation | Effect |
| --- | --- |
| New plugin, complete or incomplete scan | Tracked (up to capacity); awaits its trust check; an interval plugin is due once that check approves. |
| Plugin missing from a **complete** scan | Removed: output withdrawn, an active run cancelled (it keeps its slot until it ends). Its approval is **never** touched. |
| Plugin missing from an **incomplete** scan | Kept with its identity, last descriptor and approval as `Uncertain`: not launched, manual refreshes rejected, and **suspended**: its output is withheld until the targeted trust check requested at once confirms the approval it was produced under (then it is shown again; otherwise withdrawn). An active run is not cancelled (an incomplete listing is weak evidence), but its output is not published. Not removal, not a content change, not a revocation. The scan is retried after 5 s; a plugin already checked since it became uncertain is not checked again by the retries (periodic reconciliation still checks it). |
| **Root unavailable** (validation failed, `TW701`) or discovery failed (`TW703`) | Stronger evidence than an incomplete listing, still not removal. **Every** plugin, including one already uncertain, is suspended at once in the snapshot that reports it: output withheld, nothing launched, an active run cancelled, earlier checks stale. Identities, descriptors and approvals are kept. No trust check is made while the root is unavailable (none could verify anything); retries every 5 s repeat nothing. |
| Uncertain plugin listed again (also after root recovery) | `Present`; nothing proves it was unchanged meanwhile, so it stays suspended until a fresh trust check: then it runs again and, if the check confirms the approval its retained output was produced under, that output is shown again. |
| Different metadata or descriptor diagnostics | A **hint** that the file may have changed, not proof: diagnostics also differ when a read failed or was limited, and only the trust check's fingerprint comparison establishes a change. Conservatively, output is withdrawn and an active run cancelled at once; the trust check then decides, and the trust service invalidates the approval persistently only if the content did change. |
| Case-only rename (`Clock.5s.ps1` → `clock.5s.ps1`) | The same identity (case-insensitive): the spelling is updated, approval, output and schedule are kept. |
| Rename, including a changed interval (`clock.5s.ps1` → `clock.10s.ps1`) | The old identity is removed, the new one is a new plugin: no approval, no output, no history inherited. |
| Identity collision (case variants in a case-sensitive directory) | Discovery excludes the variants from a complete result, so the plugin is removed. |
| Unsafe or inaccessible entry | Skipped by discovery; a tracked plugin whose trust check finds its path unsafe is `Unavailable`. |
| Root replaced by another directory with the same path (different volume serial number and file identifier, `TW713`) | The watcher is recreated, and every plugin is suspended at once (output withheld, active runs cancelled) until a fresh trust check, made by the same pass after its scan, decides. Replacement alone revokes nothing: the trust service applies its normal policy to the files now at the root (unchanged files keep their approval; changed ones lose it). |

**Capacity and overload.** At most 500 plugins (discovery's default candidate limit) are tracked. Removals are applied before additions, so a complete scan makes room. Further plugins are not tracked (`UntrackedPlugins`, `TW704` once per overload); a tracked plugin, its active run and its cleanup block are never evicted to make room. Repeated incomplete scans listing different plugins therefore cannot grow the state (tested).

## File system watching

A recursive `FileSystemWatcher` on the root, with the largest buffer Windows accepts (64 KiB), reports file and directory names, content writes, size, attributes and creation time. Last access and security changes are not watched, so reads (including the scheduler's own discovery and trust checks) cause no notifications.

Notifications are **hints, never facts**. A watcher-provided name is only used to look up a tracked plugin with that identity for a trust check; it is never opened, launched or trusted.

| Notification | Effect |
| --- | --- |
| In or below a dot-prefixed entry (`.git/...`) | Ignored, as discovery ignores it (a rename *to* a visible name is not ignored). |
| Content change of a file without a plugin extension (`cache.json`), or of a directory's timestamp | Ignored: plugins may write auxiliary files without causing scans, cancellations or refreshes. |
| Content change of a plugin file | Rescan and a targeted trust check of that identity. |
| Creation, deletion or rename of anything | Rescan: the name alone cannot tell a file from a directory whose renaming changes plugin identities. |
| Buffer overflow | Full reconciliation (rescan and trust check of every plugin), `TW711` (once while one is pending). |
| Other watcher error | The watcher is recreated and a full reconciliation follows, `TW712`. Notifications of the old watcher are ignored. |
| Watcher cannot be created | Periodic reconciliation only, `TW710` once; creation is retried by every pass. |

**Debouncing.** A rescan starts when the file system has been quiet for `WatcherDebounce` (250 ms) after the last relevant notification, but at most `WatcherMaxDelay` (1 s) after the first one. A continuous storm therefore causes at most about one rescan per second, never one per notification, and never postpones reconciliation indefinitely. Only the first notification of a burst wakes the pump. At most 256 plugin identities are remembered between passes; more cause a full trust reconciliation instead.

**Windows watcher limitations.** `FileSystemWatcher` (`ReadDirectoryChangesW`) can lose notifications when its buffer overflows, reports them late, more than once or out of order, reports a move across directories as a deletion and a creation, does not report changes behind reparse points (which discovery does not follow either), and is not intended for network shares (which are rejected anyway). Periodic reconciliation covers every missed notification.

**Edits while a plugin runs.** The execution lease keeps a running plugin's file open without write, delete or rename sharing (unchanged from Phase 4). Editors therefore report the file as in use during a run; a failed save is **not** a content change and changes nothing. Changes are made between runs; tests make real edits between runs and use deterministic seams for changes during runs.

### Detection latency

| Change | Detected |
| --- | --- |
| Host operation (`NotifyTrustChanged`, `SetEnabled`) | Invalidation at once, in the call; the fresh trust check after any pass in progress. |
| `RequestReconciliation` | After any pass in progress, plus the scan and a check of every plugin. |
| File change with a notification | After the debounce (250 ms of quiet, at most 1 s), plus any pass in progress, plus the scan and the trust check. |
| Missed notification, external trust store change (another process approving or revoking) | Within one `ReconciliationInterval` (30 s) after the previous periodic pass ended, plus the duration of the pass. |

A pass's duration grows with the plugin tree (discovery reads up to 64 KiB of each candidate) and with the trust checks (each hashes a whole plugin file, up to 16 MiB, under the store lock, which can wait up to the lock timeout, 10 s by default, when another process holds it). Periodic reconciliation is independent of plugin intervals: a manual-only or 30-day plugin's output is still withdrawn within that bound when its approval disappears. Cross-process revocation is **not** instantaneous.

## Trust, approval and revocation

- **One trust service.** The scheduler uses the execution service's `PluginTrustService` for every check, so the [unsaved invalidations](trust-model.md#unsaved-invalidations) of that instance keep being enforced for scheduling, launches and publication alike (tested with a read-only store).
- **The scheduler's own checks** (`CheckTrust(root, id)`) decide eligibility only: for new plugins, after invalidations, for uncertain plugins, after a root was replaced or became available again, for watcher hints, on `RequestReconciliation` and, periodically, for every supported plugin. When such a check observes changed content, the trust service invalidates the approval persistently (or keeps its deny-only unsaved invalidation), as in Phase 3. The scheduler has no approval store of its own and never approves anything.
- **Every launch** goes through the execution engine's authoritative launch-time check and lease, with the expected approval (below).
- **Every publication** is preceded by `CheckTrust(result.Approval)`.
- **Any observation other than the same approval invalidates.** A check that returns no approval, a different approval (reapproval), or a non-approved status withdraws the output and cancels an active run. Unavailable files, unsafe paths and an unavailable or corrupt store are never a reason to keep using an earlier authorization: the plugin is `Unavailable` until a check approves it again.

### Ordering of trust observations

Trust observations come from three places: the scheduler's reconciliation checks, the engine's launch-time check (a `TrustDenied` result) and the output check before publication. Each plugin has a **trust epoch** that advances with every applied observation, every invalidation and every suspension, whether or not another check is requested. Each observation is applied with the epoch at which its check started (for a launch-time check: the dispatch):

- **Current** (the epoch did not change): applied normally.
- **Stale** (something else was applied meanwhile, so the order of the two checks is unknown): it can only take away, never overwrite a newer decision. A stale **approval** is discarded; a stale **denial** withdraws whatever approval the plugin holds, as a trust-change notification would (output withdrawn, run cancelled, a fresh check requested). When no current decision would otherwise remain, or the discarded approval might be the newer one, one fresh check is requested. That is one extra check per race, never a loop: the fresh check is current unless yet another decision intervenes.

So an approval obtained by a reconciliation check, but applied after a newer content change, unavailable store or launch-time denial, cannot restore eligibility; a newly approved generation still runs, because the check that finds it is fresh. Applying an observation never touches the trust store: persistent invalidation of changed content and the deny-only unsaved invalidations are the trust service's, made during the check. None of this is authorization: every launch still makes its own authoritative check, so a stale approval could at most have caused a launch attempt that the engine denies.

**Host ordering contract.** After `Approve` or `Revoke` on the shared trust service has **returned**, call `NotifyTrustChanged(pluginId)` (or `NotifyTrustChanged()` after replacing or editing the store). The scheduler then, in the call: withdraws the plugin's output, requests cancellation of its active run, stops launching it, and requests a fresh trust check; launches resume only if that check approves. A run that was already authorized keeps running until its cancellation takes effect; its result is never published (it is superseded, and its output check would find the approval stale anyway). Changes made by other processes are found by periodic reconciliation.

## Expected approval generations

The race: the scheduler selects a plugin while it believes approval generation A is current; meanwhile the plugin is revoked and approved again (generation B, possibly for the same bytes); then the engine makes its launch-time check. The public `RunAsync` authorizes whichever approval is current, so it would launch under B a run selected under A.

The scheduler closes this with a small **internal** extension of the existing path, not a new authorization mechanism:

- `PluginExecutionService.RunAsync(root, id, runOptions, expectedApproval, token)` (internal) runs the unchanged pipeline, and its launch-time check is `PluginTrustService.CheckTrustForExecution(root, id, expectedApproval, …)` (internal): the same `CheckCore` under the store lock, the same inspection, invalidation and lease, plus the existing `PluginTrustPolicy` condition of `CheckTrust(approvalIdentity)` that the current approval is exactly that identity (key, fingerprint and generation).
- An expected identity of another key (another plugin or root) is denied as `StaleApproval` without being compared.
- An older generation is denied (`StaleApproval`, `TrustDenied`), starts nothing and creates no lease; the scheduler then checks again, finds B and treats it as a new approval.
- Changed content is still `ContentChanged` with persistent invalidation; unsaved invalidations still deny.
- The public `RunAsync` and its semantics are unchanged. The expected identity is compared, never trusted: it is not a token, grants nothing and cannot weaken the mandatory check.

Tested with the real trust service and helper processes: old-generation rejection, exact key matching (other plugin, other root, same key spelled differently), content invalidation, unsaved invalidations, lease release and unchanged public behavior (`ExpectedApprovalExecutionTests`).

**Remaining races.** The launch-time check, its lease and process creation are as in Phase 4: an approval revoked *after* the launch-time check does not stop that run until the host's notification (or the next periodic check) cancels it, and the other windows in [execution-engine.md](execution-engine.md#remaining-time-of-check-to-time-of-use-windows) remain. A revocation after the output check and before the commit is found by the commit's revision check only if the host notified the scheduler; otherwise the output stays until the next check of that plugin (at most one reconciliation interval).

## Cancellation and stale results

Every tracked plugin has a lifecycle **revision**. An **invalidation** increases it, withdraws the plugin's output and requests cancellation of its active run. Invalidations come from: disabling, confirmed removal, a changed descriptor, a trust observation other than the same approval (revocation, reapproval, content change, unsafe or unavailable plugin, unavailable store), a stale denial, `NotifyTrustChanged`, a cleanup incident, a fault and shutdown. A root that is unavailable or replaced increases it too and cancels the run, but only suspends the output (see [Applying a scan](#applying-a-scan)).

Each attempt records the plugin state object and revision it was dispatched for. Its completion applies **only** if, under the lock, at the moment of the commit, the scheduler is running, the same plugin state object is still tracked and its revision is unchanged. Otherwise the attempt is superseded, and its late completion can never: publish output, restore a removed plugin, re-enable a disabled one, replace newer status, clear a cleanup block, reintroduce an old approval or schedule a follow-up based on it. It still records a cleanup incident, frees its slot, starts the minimum relaunch delay and, for the same plugin state, becomes its `LastRun` with disposition `Superseded`.

**Before the engine is called.** An attempt is dispatched under the lock and its worker starts on the thread pool. Before calling the engine, the worker checks under the lock that the attempt is still current. One superseded in between (disabled, removed, invalidated, suspended, stopping, faulted) never reaches the engine: it ends as a normal `Superseded` outcome with no execution status and `ProcessCleanupStatus.NotStarted`, not a fault or cleanup incident. Because its token is cancelled under the lock that superseded it, an attempt superseded just after that check reaches the engine with a token that is already cancelled, and the engine starts nothing (`Cancelled`, `NotStarted`). What remains is the engine's own window: a cancellation that arrives while process creation is under way cannot interrupt it (see [execution-engine.md](execution-engine.md#remaining-time-of-check-to-time-of-use-windows)).

Cancellation is cooperative through the engine's token: requesting it does not mean the process has exited, and the slot and the identity are held until the engine returns after its cleanup.

## Output publication

A run's output is published only if all of these hold, and the final commit of output and approval identity happens atomically with the last two checks:

1. the run succeeded with complete, parsed output (`PluginExecutionStatus.Succeeded`);
2. the result belongs to the dispatched plugin and its approval is exactly the expected approval;
3. a fresh `CheckTrust(result.Approval)` returned `Approved` for exactly that identity (the check holds the run's slot);
4. the plugin is still the same tracked state with the same revision, still eligible, and the scheduler is running;
5. it fits the aggregate output budget.

Otherwise nothing is published and earlier output is withdrawn (`NotPublished`, `TW730` or `TW731`).

**Withheld output.** Retained output is shown in `ScheduledPlugin.Output` only while no trust decision is pending for the plugin and its latest applied trust observation approves exactly the approval the output was produced under. While a plugin is suspended (missing from an incomplete scan, root unavailable or replaced, back after uncertainty) its output is therefore withheld at once, kept (and counted) but not published, and a fresh check either shows it again or withdraws it. Further rules:

- Successful **empty** output is valid and replaces earlier output.
- A **failed** run withdraws earlier output (conservative: no stale interactive menus).
- Earlier output stays visible during an ordinary refresh only while no invalidation or suspension has occurred; an invalidation withdraws it at once, a suspension withholds it at once.
- Only `ScheduledPlugin.Output` (a `PublishedPluginOutput`: output, approval identity, run identifier, time) exposes output. No other snapshot member does (`SchedulerApiTests`).
- **Actions stay inert.** Activating one in a future phase still requires `PluginTrustService.CheckTrust(output.Approval)` at that moment, as Phase 4 requires.

## Incomplete cleanup protection

When a run reports `PluginExecutionStatus.CleanupIncomplete` or `ProcessCleanupStatus.Incomplete` (or the engine failed unexpectedly, so its cleanup is unknown), processes started for the plugin may still be running. The scheduler then records an incident in a **cleanup registry** kept apart from discovery state:

- the plugin identity is never launched again by this scheduler instance: not automatically, not manually, not after disabling and enabling, reapproval, trust notifications, rescans, or removal and reappearance under the same (case-insensitive) identity;
- the incident is recorded even when the attempt was already superseded or the plugin was removed;
- each incident permanently occupies one execution slot, so repeated incidents across different plugins cannot exceed the global limit; with `MaxConcurrency` incidents nothing runs any more;
- the registry holds 16 incidents (the largest concurrency limit, so in practice it cannot fill). If an incident cannot be recorded anyway, the scheduler **fails closed**: it launches nothing more (`ExecutionHalted`, `TW721`) instead of forgetting it;
- nothing unblocks automatically; there is no timer and no recovery operation, because nothing proves those processes ended.

The registry lives in memory. A new scheduler instance, or a restarted TrayWisp, does not know earlier incidents and cannot establish that those processes ended: an operator must verify (`TW720` says so). TrayWisp never enumerates or terminates unrelated processes. The Phase 4 limits remain: processes created before job assignment, and descendants outside the job, are not tracked.

## Shutdown

`StopAsync`:

1. stops accepting work (`Stopping`; commands return `NotRunning`);
2. withdraws all published output and pending manual refreshes;
3. dispatches nothing more (a run dispatched just before never reaches the engine, or reaches it with a cancelled token and starts nothing; one already inside process creation, which cannot be interrupted, is started and terminated at once, as the engine documents);
4. requests cancellation of every active run and of the reconciliation in progress (signalled at once; callbacks run on the thread pool);
5. disposes the watcher and the timer, once, on the thread pool, so that a disposal that blocks cannot hold the call beyond its bound;
6. waits for all of it, at most `ShutdownTimeout` **measured from the call** (default 15 s, longer than the engine's 5 s cleanup grace period plus 1 s for reads) or until its token is cancelled;
7. reports a `SchedulerStopResult`.

**One shutdown.** Concurrent and repeated calls share the first call's shutdown: each waits (within its own bound and token) for the same outstanding runs, reconciliation, teardown and cancellation deliveries, and each gets a result computed from the scheduler's state at the end of its wait. No caller is told that shutdown completed while another still owns teardown work.

`IsComplete` is true only when no run, no reconciliation, no watcher or timer disposal and no cancellation delivery is outstanding, and the lifecycle is then `Stopped`; otherwise `OutstandingRuns`, `ReconciliationOutstanding` and `TeardownOutstanding` say what remains, and `TW740` is reported. Outstanding work keeps its resources (the scheduler disposes the watcher and the timer, which no worker uses, at once, and a cancellation source only when nothing can use it any more: see [Cancellation resources](#cancellation-resources)), publishes nothing and launches nothing; when it ends, the lifecycle becomes `Stopped`, and calling `StopAsync` again reports the updated result. `UnresolvedCleanupIncidents` reports incidents whose processes may still run. Some operating system calls cannot be interrupted (process creation, file reads during a trust check, termination calls, a disposal that waits for the system), so the bound limits the wait, not those calls.

### Cancellation resources

Every attempt has its own `CancellationTokenSource`, and the scheduler has one more for reconciliation. Neither has a timer or a linked token, but either can still own an operating-system resource: reading `CancellationToken.WaitHandle`, as the trust store's lock wait does while another operation holds the store lock, allocates an event that only disposal releases deterministically (otherwise it waits for finalization). The scheduler therefore owns and disposes both kinds, each exactly once, under its lock (disposal runs no callbacks; it only releases that event):

- **An attempt's source** is disposed when all of these hold: its worker has ended every use of the token (the engine call, including the engine's launch-time trust check and process cleanup, and the output trust check have returned; the engine disposes its own registrations before it returns); no `CancelAsync` delivery of it is still running its callbacks on the thread pool; and the attempt is no longer registered as active, so no later supersession, fault or shutdown can signal it. Whichever of the completion and the end of the delivery happens last disposes it. This covers a normal completion, an attempt superseded before launch (which never reached the engine), a cancelled run, a run whose process cleanup was not confirmed (the engine has returned; its cleanup block is separate state) and a completion that failed and was recovered. An attempt that stays registered after a failed completion (its cleanup incident could not be recorded) keeps its source, as it keeps its slot. A cancellation is never requested on a released source.
- **The scheduler's own source** is disposed when the lifecycle becomes `Stopped`: no reconciliation pass runs or can start, and no delivery of its cancellation is running. A scheduler stopped before it started disposes it at once. Each pass takes its token under the lock when it starts, while the source is certainly alive. Repeated and concurrent `StopAsync` and `DisposeAsync` calls signal it once (the first stop) and never touch it after it was disposed. After an incomplete shutdown it is kept until the outstanding work has ended.
- **Disposal closes the event explicitly.** After a `CancelAsync` that found no callback to run, the runtime (observed on .NET 10) leaves the source in its internal "notifying" state, in which `Dispose` does not close the source's event and leaves it to finalization. So the scheduler first disposes the `WaitHandle` of a cancelled source itself (allocating and closing one at once if none existed), then the source. This is safe only because no callback of the source can still run at that point.

## Faults

Expected operational failures are results: an unavailable or unsafe root (`TW701`), discovery I/O failures (`TW703`), trust check I/O failures (`TW750`, the plugin is `Unavailable`), watcher failures (`TW710`–`TW712`). An **unexpected** exception (a programming error) in the engine, a pass, a decision, a watcher callback, a run's completion, a pass's finalization, a cancellation callback or the disposal of the watcher or timer moves the scheduler to `Faulted` (or, while stopping, halts it): `TW700` with the exception type, HRESULT and where it happened (never its message), output withdrawn, active work cancelled, nothing more launched or published, and the affected plugin blocked if its cleanup is unknown. A faulted scheduler never looks healthy: it shows nothing as queued or due and dispatches nothing.

Failures cannot lose state:

- A completion records an unresolved cleanup **before** it releases the attempt's slot. If the completion fails part-way, its handler records the incident if that has not happened yet; if even that is impossible, the attempt stays registered, keeps its slot and is reported as outstanding by `StopAsync`, rather than being forgotten.
- A failed pass finalization still ends the pass, so it is not reported as in progress forever.
- Every run and pass task is observed: a fault raised after its own handling (for example by the handler of a failed completion) still faults the scheduler.

`OutOfMemoryException` is not caught. Consumers are never called back, so they cannot fault the scheduler.

## Limits and defaults

| Limit | Value | When exceeded |
| --- | --- | --- |
| Concurrent attempts (`MaxConcurrency`) | 4 (1–16) | the plugin waits (`Queued`) |
| Attempts per plugin identity | 1 | the plugin waits |
| Pending manual refreshes per plugin | 1 | `Coalesced` |
| Minimum relaunch delay | 250 ms | the plugin waits |
| Failure backoff | 5 s doubling to 60 s, never shorter than the interval | — |
| Tracked plugins (`MaxTrackedPlugins`) | 500 | not tracked, `TW704` |
| Cleanup incidents (`MaxCleanupIncidents`) | 16, each occupying a slot | execution halted, `TW721` |
| Watcher hints between passes (`MaxWatcherHints`) | 256 identities | full trust reconciliation |
| Watcher debounce / maximum delay | 250 ms (0–10 s) / 1 s (debounce–60 s) | — |
| Periodic reconciliation (`ReconciliationInterval`) | 30 s (1 s–1 h) | — |
| Incomplete-scan retry | 5 s | — |
| Reconciliation passes in progress | 1 | requests are merged |
| Scheduler diagnostics kept (`MaxDiagnostics`) | 64 most recent, `TW799` for dropped ones | oldest dropped |
| Discovery diagnostics kept per scan (`MaxDiscoveryDiagnostics`) | 64 | `DiagnosticsTruncated` |
| Diagnostics per run summary (`MaxRunDiagnostics`) | 8 execution, 8 trust, 8 output | `DiagnosticsTruncated` |
| Retained output (`MaxRetainedOutputCost`) | 32 MiB estimated | not published, `TW731` |
| Shutdown wait (`ShutdownTimeout`) | 15 s (0–5 min) | incomplete result, `TW740` |
| Run timeout (`RunOptions.Timeout`) | the engine's default (30 s), 1 s–5 min | the run times out |
| Snapshot notifications | one pending signal shared by all waiters | coalesced |

**Retained output.** The parser bounds one output (262,144 characters, 2,048 menu nodes, depth 8, 64 titles, 32 command arguments per item), but 500 plugins could still retain a lot. The budget uses `PluginSchedulingPolicy.EstimateRetainedOutputCost`, computed from the retained object model rather than from the captured bytes: 1 KiB for the output and its publication record; 128 bytes per title and node; per string (label, tooltip, font, command executable and **each command argument**) 32 bytes plus 2 per UTF-16 code unit; per non-empty submenu or argument list 32 bytes plus 8 per element; 96 bytes per non-default format and per colour; 48 per command or URL action; and per URL 192 bytes plus three copies of its text. Shared instances (default format, refresh action, empty arrays) cost nothing, so complete empty output costs 1 KiB and stays publishable. It is a budget estimate, not a measurement of the CLR heap; it is pure, linear in the model, and saturates instead of overflowing. 32 MiB admits hundreds of typical outputs, and more than ten of the largest argument-heavy outputs the parser accepts (each about 1.3 MB). Withheld output still counts, because it is still retained. Snapshots that consumers keep are outside this accounting: a consumer that holds old snapshots keeps their output alive. Raw standard output, standard error and logs are not retained.

## Diagnostics

Scheduler diagnostics (`SchedulerDiagnostic`) have their own range. Discovery (`TW100`–`TW399`), output (`TW400`–`TW499`), trust (`TW500`–`TW599`) and execution (`TW600`–`TW699`) diagnostics are passed on unchanged in `Discovery.Diagnostics` and `LastRun`. Messages never contain script content, output, standard error, environment values or exception messages; paths are quoted and escaped with `PluginDiagnosticText`. Repeated conditions (root unavailable, incomplete scans, watcher unavailable, capacity, overflows while a reconciliation is pending) are reported once per occurrence, not once per retry.

| Code | Name | Severity | When |
| --- | --- | --- | --- |
| TW700 | SchedulerFaulted | Error | an unexpected internal failure; the scheduler is `Faulted` |
| TW701 | RootUnavailable | Warning | the root cannot be validated or scanned (includes the trust diagnostic code, such as `TW502` or `TW504`) |
| TW702 | DiscoveryIncomplete | Warning | a scan was incomplete; missing plugins are uncertain |
| TW703 | DiscoveryFailed | Error | discovery failed with an I/O error |
| TW704 | TrackingCapacityReached | Warning | more plugins than the scheduler tracks |
| TW710 | WatcherUnavailable | Warning | the watcher cannot be created |
| TW711 | WatcherOverflow | Warning | notifications were lost |
| TW712 | WatcherFailed | Warning | the watcher failed and is recreated |
| TW713 | RootReplaced | Info | the root directory was replaced |
| TW720 | CleanupIncomplete | Error | a plugin is blocked for the scheduler's lifetime |
| TW721 | CleanupRegistryFull | Error | an incident could not be recorded; execution halted |
| TW730 | OutputNotPublished | Warning | a successful run was not published (superseded, trust check, stopping) |
| TW731 | OutputBudgetExceeded | Warning | the retained-output budget is exhausted |
| TW740 | ShutdownIncomplete | Error | shutdown ended with work outstanding |
| TW750 | TrustCheckFailed | Warning | a scheduler trust check failed with an I/O error |
| TW799 | DiagnosticLimitReached | Info | older scheduler diagnostics were dropped |

## Requirements for future CLI and UI consumers

*Planned obligations for Phases 6 to 9.*

- Create one `PluginTrustService`, one `PluginExecutionService` on it and one `PluginScheduler` per root, for the host's lifetime. Never run a second scheduler for the same root.
- Read `Snapshot`, wait with `WaitForChangeAsync(snapshot.Revision, token)`, and redraw from the newest snapshot; never cache output across revisions or treat it, or `ObservedTrustStatus`, as authorization.
- Before activating an output action, call `trust.CheckTrust(output.Approval)` and act only on `Approved`.
- Call `NotifyTrustChanged` after every approval or revocation made through the shared trust service.
- Show `CleanupBlocked`, `CleanupBlocks`, `ExecutionHalted` and `Faulted` prominently: processes may still run, and only an operator can verify them.
- Show `Uncertain` as distinct from removed, and diagnostics escaped, as untrusted text.
- Call `StopAsync` on exit and report an incomplete result.
- Explain that approval is consent, not sandboxing, and that editors may report a running plugin's file as in use.

## Remaining limitations

- No host uses the scheduler yet; the CLI and desktop application are unchanged.
- Enabled state and cleanup incidents are in memory only and per instance; there is no persistent settings subsystem. A plugin removed by a complete scan and reappearing is enabled again.
- Detection of external trust store changes and missed notifications is periodic (see [Detection latency](#detection-latency)).
- A transient `PluginUnavailable` observation (for example a file briefly locked by another program during a periodic check) withdraws output and cancels a run; the plugin runs again once a later check approves it.
- After a transient trust denial (unavailable store or file) that is not a stale observation, no extra check is requested: the plugin is found approved again by the next periodic reconciliation, a watcher hint or `RequestReconciliation`.
- Ordering of trust observations is by when their checks started, per plugin; when two checks overlap, the scheduler cannot know which observed the store later and resolves the ambiguity toward denial plus one fresh check.
- A cancellation that arrives while the engine is creating the process cannot interrupt that call (unchanged from Phase 4).
- The retained-output cost is an estimate of the scheduler-owned model, not a heap measurement, and excludes snapshots consumers keep.
- Filesystem race limitations of discovery and trust validation, the post-start job assignment window, untracked descendants, and the deferred `PSModulePath` compatibility concern of Windows PowerShell started from PowerShell 7 (see [execution-engine.md](execution-engine.md#real-interpreter-tests)) are unchanged.
- The scheduler does not keep standard error; the latest run's bounded diagnostics are its only run information (bounded local diagnostics storage is Phase 6).
- A cancellation source whose owner never ends is left to finalization: the source of an attempt that stays registered after a failed completion, and the scheduler's own source after a shutdown whose outstanding work never ends (for example a call that cannot be interrupted). If even the handler of a failed completion fails, the attempt's source is kept too, unless the attempt is no longer registered.

## Tests

`TrayWisp.Core.Tests/Scheduling` (policy) and `TrayWisp.Runtime.Tests/Scheduling` and `…/Execution/ExpectedApprovalExecutionTests`:

- **Deterministic scheduler tests** run the real scheduler on fake discovery, trust and execution operations (an in-memory file system and trust store that invalidates and checks generations like the real service, and runs that the test completes) and a manual `TimeProvider` (no new dependency). Blocking points hold a trust check before it starts or after its result was obtained, a scan after its listing, the watcher's disposal, and (internal test hooks) an attempt between dispatch and the launch decision or the engine call; injected failures cover completion, its handler, pass finalization, cancellation callbacks and disposal. `SchedulerTrustOrderingTests`, `SchedulerSuspensionTests` and `SchedulerLifecycleRaceTests` inspect the snapshots while those points are held. A test helper advances the clock one timer at a time and waits until no decision, pass or completion is in progress (an internal activity signal), so time-based behavior is exact to the millisecond and independent of machine speed. They cover timing (immediate first run, manual-only plugins, intervals measured from completion, 250 ms and 30 days, no catch-up, backoff and reset, clock jumps), manual refresh and coalescing, the concurrency limit, per-plugin exclusivity, fairness, discovery reconciliation, uncertainty, root loss and replacement, watcher classification, debouncing, storms and overflow, trust changes, generation races, publication races, stale completions, cleanup blocking, faults, shutdown, concurrent requests from several threads, and the lifetime of cancellation sources (`SchedulerCancellationResourceTests`: an event allocated through `WaitHandle` is closed after completion, after cancellation callbacks and execution both ended, after supersession before launch, and after complete shutdown, never earlier, and is kept by incomplete shutdown and by an attempt that stays registered).
- **Integration tests** run the real scheduler with a temporary root, an isolated trust store, the real watcher, discovery, trust service and engine, and the helper program as interpreter: end-to-end discovery, approval, execution, publication, modification, reapproval, manual refresh and shutdown; an unapproved plugin that genuinely never starts (a marker file and the process-start counter); the watcher finding a new and a removed plugin with periodic reconciliation effectively disabled; repeated interval runs and termination of a plugin and its descendant on shutdown (checked by process ID); unsaved invalidations of the shared trust service; trust store lock contention during the reconciliation, launch-time and output trust checks (the store waits on the scheduler's tokens), with the events this allocates released after the run and after shutdown; same-length, same-timestamp and metadata-only edits; and a root that disappears and returns.
- Waits are event-driven (snapshot revisions) and bounded; no test sleeps for scheduling. The scheduler tests were repeated many times, also with several runs in parallel, to detect races.
