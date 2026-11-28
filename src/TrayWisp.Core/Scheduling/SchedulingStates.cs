namespace TrayWisp.Core.Scheduling;

/// <summary>The lifecycle of one scheduler instance. A scheduler is single-use: it is started once and stopped once.</summary>
public enum SchedulerLifecycle
{
    /// <summary>Created, not started yet. Nothing is discovered, watched or run.</summary>
    NotStarted,

    /// <summary>Discovering, watching, reconciling and running plugins.</summary>
    Running,

    /// <summary>
    /// An unexpected internal failure stopped scheduling. Nothing more is launched or published and active runs were
    /// cancelled; the host must still stop the scheduler to wait for them.
    /// </summary>
    Faulted,

    /// <summary>Shutdown has begun: nothing new is launched or published, and active work is being cancelled and awaited.</summary>
    Stopping,

    /// <summary>
    /// Shut down, with no outstanding run, reconciliation, watcher or timer disposal, or cancellation delivery. The
    /// instance cannot be started again.
    /// </summary>
    Stopped,
}

/// <summary>Whether the latest discovery confirmed a tracked plugin.</summary>
public enum PluginPresence
{
    /// <summary>The latest discovery scan listed the plugin.</summary>
    Present,

    /// <summary>
    /// The plugin was missing from an incomplete scan, or the root could not be scanned. This is not removal: the
    /// plugin is kept with its last known descriptor and approval, but nothing launches it until a scan lists it again
    /// and a fresh trust check approves it, and its output is withheld until a trust check made since confirms it.
    /// </summary>
    Uncertain,
}

/// <summary>Whether a plugin has work in progress.</summary>
public enum PluginActivity
{
    /// <summary>No run is active or waiting.</summary>
    None,

    /// <summary>A run is due (or manually requested) and waits for an execution slot or the minimum relaunch delay.</summary>
    Queued,

    /// <summary>A run is active.</summary>
    Running,

    /// <summary>A run is active and its cancellation has been requested. It still occupies its execution slot until it ends.</summary>
    Cancelling,
}

/// <summary>Whether a plugin may be launched now, or the most important reason why not, in this order of precedence.</summary>
public enum PluginEligibility
{
    /// <summary>The plugin may be launched (subject to its schedule, the concurrency limit and the launch-time trust check).</summary>
    Eligible,

    /// <summary>An earlier run's process cleanup could not be confirmed. Never launched again by this scheduler instance.</summary>
    CleanupBlocked,

    /// <summary>The plugin's language has no execution support (Bash, batch).</summary>
    UnsupportedLanguage,

    /// <summary>The host disabled the plugin.</summary>
    Disabled,

    /// <summary>The plugin was missing from an incomplete scan, or the root could not be scanned.</summary>
    Uncertain,

    /// <summary>The plugin's trust has not been checked since it was discovered or since its last invalidation.</summary>
    AwaitingTrustCheck,

    /// <summary>The latest trust check found no current approval (never approved, revoked, invalidated or replaced).</summary>
    PendingApproval,

    /// <summary>The latest trust check could not verify the plugin (unavailable file, unsafe path, unavailable trust store).</summary>
    Unavailable,
}

/// <summary>
/// One summary state of a plugin for display, derived from its eligibility, activity and last run by
/// <see cref="PluginSchedulingPolicy.GetStatus"/>. A reason that prevents launching always wins over activity.
/// </summary>
public enum PluginScheduleStatus
{
    /// <summary>Eligible and waiting for its next refresh; the last run, if any, succeeded.</summary>
    Idle,

    /// <summary>Eligible, due and waiting for an execution slot.</summary>
    Queued,

    /// <summary>Eligible and running.</summary>
    Running,

    /// <summary>Eligible, but the last run failed or its output was not published.</summary>
    Failed,

    /// <summary>See <see cref="PluginEligibility.CleanupBlocked"/>.</summary>
    CleanupBlocked,

    /// <summary>See <see cref="PluginEligibility.UnsupportedLanguage"/>.</summary>
    UnsupportedLanguage,

    /// <summary>See <see cref="PluginEligibility.Disabled"/>.</summary>
    Disabled,

    /// <summary>See <see cref="PluginEligibility.Uncertain"/>.</summary>
    Uncertain,

    /// <summary>See <see cref="PluginEligibility.AwaitingTrustCheck"/>.</summary>
    AwaitingTrustCheck,

    /// <summary>See <see cref="PluginEligibility.PendingApproval"/>.</summary>
    PendingApproval,

    /// <summary>See <see cref="PluginEligibility.Unavailable"/>.</summary>
    Unavailable,
}

/// <summary>Why a run was started.</summary>
public enum RefreshTrigger
{
    /// <summary>The plugin's refresh interval elapsed, or it became eligible.</summary>
    Automatic,

    /// <summary>The host requested a refresh.</summary>
    Manual,
}

/// <summary>The outcome of a manual refresh request.</summary>
public enum RefreshRequestStatus
{
    /// <summary>A refresh is now pending. It runs as soon as the plugin is eligible, has no active run and a slot is free.</summary>
    Accepted,

    /// <summary>A manual refresh was already pending; the requests were merged into one.</summary>
    Coalesced,

    /// <summary>The scheduler is not running.</summary>
    NotRunning,

    /// <summary>The scheduler does not track a plugin with this identity.</summary>
    UnknownPlugin,

    /// <summary>The plugin is disabled.</summary>
    Disabled,

    /// <summary>The plugin's language has no execution support.</summary>
    UnsupportedLanguage,

    /// <summary>The plugin is blocked because an earlier run's cleanup could not be confirmed.</summary>
    CleanupBlocked,

    /// <summary>The plugin was missing from an incomplete scan, or the root could not be scanned.</summary>
    Uncertain,

    /// <summary>The latest trust check found no current approval.</summary>
    NotApproved,

    /// <summary>The latest trust check could not verify the plugin.</summary>
    Unavailable,

    /// <summary>The scheduler launches nothing more (its cleanup registry is full, or it faulted).</summary>
    ExecutionHalted,
}

/// <summary>The outcome of a host command for one plugin, such as enabling, disabling or a trust notification.</summary>
public enum PluginCommandStatus
{
    /// <summary>The command changed the plugin's state.</summary>
    Applied,

    /// <summary>The plugin was already in the requested state.</summary>
    Unchanged,

    /// <summary>The scheduler is not running.</summary>
    NotRunning,

    /// <summary>The scheduler does not track a plugin with this identity.</summary>
    UnknownPlugin,
}

/// <summary>What the scheduler did with a completed run.</summary>
public enum PluginRunDisposition
{
    /// <summary>The run succeeded and its output was published.</summary>
    Published,

    /// <summary>The run succeeded, but its output was not published (a scheduler diagnostic says why) and earlier output was cleared.</summary>
    NotPublished,

    /// <summary>The run did not succeed; earlier output was cleared.</summary>
    Failed,

    /// <summary>
    /// The run was superseded while it was active (invalidation, suspension, disabling, shutdown, fault), possibly before
    /// the execution engine was called, in which case nothing was started. Its result changed nothing.
    /// </summary>
    Superseded,

    /// <summary>The execution engine failed unexpectedly; its process cleanup is unknown, so the plugin is blocked.</summary>
    Faulted,
}
