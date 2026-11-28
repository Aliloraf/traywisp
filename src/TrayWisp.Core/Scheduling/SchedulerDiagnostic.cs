using System.Globalization;
using TrayWisp.Core.Plugins;

namespace TrayWisp.Core.Scheduling;

/// <summary>
/// Stable identifiers for scheduler diagnostics, shown as <c>TW700</c>–<c>TW799</c>. The range is separate from
/// plugin (<c>TW100</c>–<c>TW399</c>), output (<c>TW400</c>–<c>TW499</c>), trust (<c>TW500</c>–<c>TW599</c>) and
/// execution (<c>TW600</c>–<c>TW699</c>) diagnostics, which the scheduler passes on unchanged. Numeric values are part
/// of the documented format (see docs/scheduling.md) and must not be renumbered.
/// </summary>
public enum SchedulerDiagnosticCode
{
    /// <summary>
    /// An unexpected internal failure (a programming error) stopped the scheduler from working. It launches nothing
    /// more and publishes no output; it must be stopped.
    /// </summary>
    SchedulerFaulted = 700,

    /// <summary>
    /// The plugin root could not be validated or scanned (missing, inaccessible, unsafe). Known plugins are kept as
    /// uncertain and suspended: output withheld, active runs cancelled, nothing launched.
    /// </summary>
    RootUnavailable = 701,

    /// <summary>A discovery scan was incomplete. Plugins missing from it are kept as uncertain, not removed.</summary>
    DiscoveryIncomplete = 702,

    /// <summary>A discovery scan failed with an operational error. Known plugins are kept as uncertain.</summary>
    DiscoveryFailed = 703,

    /// <summary>Discovery found more plugins than the scheduler tracks. The excess plugins are not scheduled.</summary>
    TrackingCapacityReached = 704,

    /// <summary>The file system watcher could not be started. Changes are detected by periodic reconciliation only.</summary>
    WatcherUnavailable = 710,

    /// <summary>The file system watcher lost notifications (its buffer overflowed). A full reconciliation follows.</summary>
    WatcherOverflow = 711,

    /// <summary>The file system watcher failed. It is recreated, and a full reconciliation follows.</summary>
    WatcherFailed = 712,

    /// <summary>
    /// The plugin root directory was replaced by another directory with the same path. Watching restarts, and every plugin
    /// is suspended (output withheld, active runs cancelled) until a fresh trust check decides.
    /// </summary>
    RootReplaced = 713,

    /// <summary>
    /// A run's process cleanup could not be confirmed. The plugin is never launched again by this scheduler instance,
    /// and one execution slot stays reserved for the incident.
    /// </summary>
    CleanupIncomplete = 720,

    /// <summary>The registry of unresolved cleanup incidents is full. The scheduler launches nothing more.</summary>
    CleanupRegistryFull = 721,

    /// <summary>A successful run's output was not published (superseded attempt, failed trust check, scheduler stopping).</summary>
    OutputNotPublished = 730,

    /// <summary>A successful run's output was not published because the aggregate budget for retained output is exhausted.</summary>
    OutputBudgetExceeded = 731,

    /// <summary>
    /// Shutdown did not finish within its bound: some runs, a reconciliation, the watcher or timer disposal, or a
    /// cancellation delivery were still outstanding.
    /// </summary>
    ShutdownIncomplete = 740,

    /// <summary>A trust check made by the scheduler failed with an operational error. The plugin is treated as unavailable.</summary>
    TrustCheckFailed = 750,

    /// <summary>Earlier scheduler diagnostics were dropped because only the most recent ones are kept.</summary>
    DiagnosticLimitReached = 799,
}

/// <summary>
/// A structured message produced by the scheduler. Messages never contain script content, plugin output, standard
/// error or environment values. Paths in them are length-limited and escaped with <see cref="PluginDiagnosticText"/>.
/// </summary>
/// <param name="Severity">How serious the diagnostic is.</param>
/// <param name="Code">Stable diagnostic identifier.</param>
/// <param name="Message">Human-readable English description.</param>
/// <param name="PluginId">The plugin the diagnostic is about, if any.</param>
public sealed record SchedulerDiagnostic(PluginDiagnosticSeverity Severity, SchedulerDiagnosticCode Code, string Message, PluginId? PluginId = null)
{
    /// <summary>Formats the diagnostic as <c>Severity TWcode: message</c>.</summary>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Severity} TW{(int)Code}: {Message}");
}
