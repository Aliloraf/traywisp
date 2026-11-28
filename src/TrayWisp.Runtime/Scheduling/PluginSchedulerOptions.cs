using TrayWisp.Runtime.Execution;

namespace TrayWisp.Runtime.Scheduling;

/// <summary>
/// Trusted host configuration for <see cref="PluginScheduler"/>. Nothing here comes from plugins, their metadata or
/// their output. Every value is validated when the scheduler is created (see docs/scheduling.md, "Limits").
/// </summary>
public sealed record PluginSchedulerOptions
{
    /// <summary>The default settings.</summary>
    public static PluginSchedulerOptions Default { get; } = new();

    /// <summary>Smallest accepted <see cref="MaxConcurrency"/>.</summary>
    public const int MinConcurrency = 1;

    /// <summary>Largest accepted <see cref="MaxConcurrency"/>.</summary>
    public const int MaxConcurrencyLimit = 16;

    /// <summary>Longest accepted <see cref="WatcherDebounce"/>: 10 seconds.</summary>
    public static TimeSpan MaxWatcherDebounce { get; } = TimeSpan.FromSeconds(10);

    /// <summary>Longest accepted <see cref="WatcherMaxDelay"/>: 60 seconds.</summary>
    public static TimeSpan MaxWatcherMaxDelay { get; } = TimeSpan.FromSeconds(60);

    /// <summary>Shortest accepted <see cref="ReconciliationInterval"/>: 1 second.</summary>
    public static TimeSpan MinReconciliationInterval { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest accepted <see cref="ReconciliationInterval"/>: 1 hour.</summary>
    public static TimeSpan MaxReconciliationInterval { get; } = TimeSpan.FromHours(1);

    /// <summary>Longest accepted <see cref="ShutdownTimeout"/>: 5 minutes.</summary>
    public static TimeSpan MaxShutdownTimeout { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many runs may be in progress at once, between <see cref="MinConcurrency"/> and
    /// <see cref="MaxConcurrencyLimit"/>; default 4. A run occupies its slot from dispatch until its process cleanup and
    /// the scheduler's trust check of its output have ended. Each unresolved cleanup incident permanently occupies one
    /// slot as well.
    /// </summary>
    public int MaxConcurrency { get; init; } = 4;

    /// <summary>
    /// How long the file system has to be quiet after a relevant change notification before a rescan starts: default
    /// 250 ms, at most <see cref="MaxWatcherDebounce"/>.
    /// </summary>
    public TimeSpan WatcherDebounce { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The longest a rescan is postponed by continuing notifications, measured from the first one: default 1 s, at least
    /// <see cref="WatcherDebounce"/> and at most <see cref="MaxWatcherMaxDelay"/>.
    /// </summary>
    public TimeSpan WatcherMaxDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How often a full reconciliation runs (a discovery scan and a trust check of every tracked plugin), measured from
    /// the end of the previous one: default 30 s, between <see cref="MinReconciliationInterval"/> and
    /// <see cref="MaxReconciliationInterval"/>. Independent of plugin refresh intervals.
    /// </summary>
    public TimeSpan ReconciliationInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long <see cref="PluginScheduler.StopAsync"/> waits for cancelled runs and a reconciliation in progress to end:
    /// default 15 s (longer than the execution engine's cleanup grace period), at most <see cref="MaxShutdownTimeout"/>.
    /// Zero does not wait.
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>Settings for every run, such as its timeout; <see cref="PluginRunOptions.Default"/> when <see langword="null"/>.</summary>
    public PluginRunOptions? RunOptions { get; init; }
}
