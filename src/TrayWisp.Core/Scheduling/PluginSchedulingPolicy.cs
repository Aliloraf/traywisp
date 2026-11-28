using System.Collections.Immutable;
using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Trust;

namespace TrayWisp.Core.Scheduling;

/// <summary>The facts that decide a plugin's <see cref="PluginEligibility"/>.</summary>
/// <param name="CleanupBlocked">An unresolved cleanup incident is recorded for the plugin's identity.</param>
/// <param name="LanguageSupported">The plugin's language has execution support.</param>
/// <param name="Enabled">The host has not disabled the plugin.</param>
/// <param name="Presence">Whether the latest scan confirmed the plugin.</param>
/// <param name="AwaitingTrustCheck">No trust check has been applied since discovery or the latest invalidation.</param>
/// <param name="ObservedTrust">The status of the latest applied trust check, or <see langword="null"/> when there is none.</param>
public readonly record struct PluginEligibilityFacts(
    bool CleanupBlocked,
    bool LanguageSupported,
    bool Enabled,
    PluginPresence Presence,
    bool AwaitingTrustCheck,
    PluginTrustStatus? ObservedTrust);

/// <summary>
/// Pure scheduling decisions: refresh delays, failure backoff, eligibility, display status and the cost of retained
/// output. Runtime measures time and applies them; nothing here reads a clock. See docs/scheduling.md.
/// </summary>
public static class PluginSchedulingPolicy
{
    /// <summary>The first automatic retry delay after a failed run: 5 seconds, or the refresh interval when that is longer.</summary>
    public static TimeSpan MinimumFailureDelay { get; } = TimeSpan.FromSeconds(5);

    /// <summary>The largest failure backoff: 60 seconds. A longer refresh interval still applies.</summary>
    public static TimeSpan MaximumFailureDelay { get; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The shortest time between the end of one run of a plugin and the start of its next run, for automatic and manual
    /// refreshes alike: 250 milliseconds, the shortest refresh interval.
    /// </summary>
    public static TimeSpan MinimumRelaunchDelay { get; } = RefreshInterval.Minimum;

    /// <summary>
    /// The failure backoff after <paramref name="consecutiveFailures"/> failed runs in a row: 5 s, 10 s, 20 s, 40 s,
    /// then 60 s. Zero when there was no failure.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="consecutiveFailures"/> is negative.</exception>
    public static TimeSpan GetFailureBackoff(int consecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(consecutiveFailures);
        if (consecutiveFailures == 0)
        {
            return TimeSpan.Zero;
        }

        // 5 s · 2^(n-1), capped before it can overflow.
        long ticks = MinimumFailureDelay.Ticks;
        for (int i = 1; i < consecutiveFailures && ticks < MaximumFailureDelay.Ticks; i++)
        {
            ticks *= 2;
        }

        return TimeSpan.FromTicks(Math.Min(ticks, MaximumFailureDelay.Ticks));
    }

    /// <summary>
    /// The delay from the end of a run (including its cleanup) to the next automatic run. Fixed-delay scheduling: a
    /// success waits the refresh interval; a failure waits the longer of the interval and the failure backoff, so a
    /// failing plugin is never retried faster than its interval. Missed intervals are never caught up. A plugin without
    /// an interval (manual refresh only) has no automatic run and therefore no automatic retry.
    /// </summary>
    /// <param name="interval">The plugin's refresh interval, or <see langword="null"/> for manual refresh only.</param>
    /// <param name="succeeded">Whether the run succeeded and its output was published.</param>
    /// <param name="consecutiveFailures">Failed runs in a row, including this one when it failed.</param>
    /// <returns>The delay, or <see langword="null"/> when no automatic run follows.</returns>
    public static TimeSpan? GetNextAutomaticDelay(RefreshInterval? interval, bool succeeded, int consecutiveFailures)
    {
        if (interval is null)
        {
            return null;
        }

        if (succeeded)
        {
            return interval.Duration;
        }

        TimeSpan backoff = GetFailureBackoff(Math.Max(consecutiveFailures, 1));
        return backoff > interval.Duration ? backoff : interval.Duration;
    }

    /// <summary>Decides whether a plugin may be launched, applying the precedence documented on <see cref="PluginEligibility"/>.</summary>
    public static PluginEligibility EvaluateEligibility(PluginEligibilityFacts facts)
    {
        if (facts.CleanupBlocked)
        {
            return PluginEligibility.CleanupBlocked;
        }

        if (!facts.LanguageSupported)
        {
            return PluginEligibility.UnsupportedLanguage;
        }

        if (!facts.Enabled)
        {
            return PluginEligibility.Disabled;
        }

        if (facts.Presence == PluginPresence.Uncertain)
        {
            return PluginEligibility.Uncertain;
        }

        if (facts.AwaitingTrustCheck || facts.ObservedTrust is null)
        {
            return PluginEligibility.AwaitingTrustCheck;
        }

        return facts.ObservedTrust.Value switch
        {
            PluginTrustStatus.Approved => PluginEligibility.Eligible,
            PluginTrustStatus.PendingApproval or PluginTrustStatus.ContentChanged or PluginTrustStatus.StaleApproval => PluginEligibility.PendingApproval,
            _ => PluginEligibility.Unavailable,
        };
    }

    /// <summary>
    /// The display status: a reason that prevents launching wins; otherwise the activity; otherwise whether the last
    /// completed run (if any) failed.
    /// </summary>
    public static PluginScheduleStatus GetStatus(PluginEligibility eligibility, PluginActivity activity, PluginRunDisposition? lastRun) =>
        eligibility switch
        {
            PluginEligibility.CleanupBlocked => PluginScheduleStatus.CleanupBlocked,
            PluginEligibility.UnsupportedLanguage => PluginScheduleStatus.UnsupportedLanguage,
            PluginEligibility.Disabled => PluginScheduleStatus.Disabled,
            PluginEligibility.Uncertain => PluginScheduleStatus.Uncertain,
            PluginEligibility.AwaitingTrustCheck => PluginScheduleStatus.AwaitingTrustCheck,
            PluginEligibility.PendingApproval => PluginScheduleStatus.PendingApproval,
            PluginEligibility.Unavailable => PluginScheduleStatus.Unavailable,
            _ => activity switch
            {
                PluginActivity.Running or PluginActivity.Cancelling => PluginScheduleStatus.Running,
                PluginActivity.Queued => PluginScheduleStatus.Queued,
                _ => lastRun is PluginRunDisposition.Failed or PluginRunDisposition.NotPublished or PluginRunDisposition.Faulted
                    ? PluginScheduleStatus.Failed
                    : PluginScheduleStatus.Idle,
            },
        };

    /// <summary>The rejection a manual refresh request gets for a plugin that is not eligible, or <see langword="null"/> when it is accepted.</summary>
    /// <remarks>A plugin awaiting its trust check is accepted: the request runs only if that check approves it.</remarks>
    public static RefreshRequestStatus? GetManualRefreshRejection(PluginEligibility eligibility) =>
        eligibility switch
        {
            PluginEligibility.Eligible or PluginEligibility.AwaitingTrustCheck => null,
            PluginEligibility.CleanupBlocked => RefreshRequestStatus.CleanupBlocked,
            PluginEligibility.UnsupportedLanguage => RefreshRequestStatus.UnsupportedLanguage,
            PluginEligibility.Disabled => RefreshRequestStatus.Disabled,
            PluginEligibility.Uncertain => RefreshRequestStatus.Uncertain,
            PluginEligibility.PendingApproval => RefreshRequestStatus.NotApproved,
            _ => RefreshRequestStatus.Unavailable,
        };

    /// <summary>
    /// Whether a run's outcome means its process cleanup was not confirmed, so that processes started for the plugin
    /// may still be running: <see cref="PluginExecutionStatus.CleanupIncomplete"/> or <see cref="ProcessCleanupStatus.Incomplete"/>.
    /// </summary>
    public static bool IsCleanupUnresolved(PluginExecutionStatus status, ProcessCleanupStatus cleanup) =>
        status == PluginExecutionStatus.CleanupIncomplete || cleanup == ProcessCleanupStatus.Incomplete;

    /// <summary>Estimated bytes of the output object graph's fixed part: the output, its two arrays and its publication record.</summary>
    public const long OutputBaseCost = 1024;

    /// <summary>Estimated bytes per title or menu node: the node object, its slot in its array and, for a title, its wrapper.</summary>
    public const long NodeCost = 128;

    /// <summary>Estimated bytes per retained string, in addition to two bytes per UTF-16 code unit: object header and length.</summary>
    public const long StringCost = 32;

    /// <summary>Estimated bytes per non-empty array (submenu or argument list), in addition to <see cref="ReferenceCost"/> per element.</summary>
    public const long ArrayCost = 32;

    /// <summary>Estimated bytes per array element reference.</summary>
    public const long ReferenceCost = 8;

    /// <summary>Estimated bytes of a formatting record other than <see cref="TextFormat.Default"/> (which is shared), and of a colour record.</summary>
    public const long FormatCost = 96;

    /// <summary>Estimated bytes of an action object; <see cref="RefreshAction"/> is shared and costs nothing.</summary>
    public const long ActionCost = 48;

    /// <summary>Estimated bytes of a <see cref="Uri"/> object, in addition to three copies of its text (original, parsed and absolute forms).</summary>
    public const long UriCost = 192;

    /// <summary>
    /// A conservative estimate, in bytes, of the memory a retained output occupies, computed from the retained object
    /// model: <see cref="OutputBaseCost"/>; per title and menu node <see cref="NodeCost"/>; per string (label, tooltip,
    /// font, command executable and each command argument) <see cref="StringCost"/> plus two bytes per UTF-16 code unit;
    /// per non-empty submenu and argument list <see cref="ArrayCost"/> plus <see cref="ReferenceCost"/> per element; per
    /// non-default format and colour <see cref="FormatCost"/>; per command or URL action <see cref="ActionCost"/>; per URL
    /// <see cref="UriCost"/> plus three copies of its text. Shared instances (the default format, the refresh action,
    /// empty arrays) cost nothing. Used for the scheduler's aggregate output budget.
    /// </summary>
    /// <remarks>
    /// This is an estimate for a budget, not a measurement of the CLR heap: object layouts, alignment and runtime
    /// versions differ, and strings that happen to be shared are counted once per reference. It is pure, allocation-free
    /// and linear in the size of the model. The parser bounds the model (input length, nodes, depth, arguments), so the
    /// result is small; the arithmetic saturates at <see cref="long.MaxValue"/> instead of overflowing anyway.
    /// </remarks>
    public static long EstimateRetainedOutputCost(PluginOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);
        long cost = OutputBaseCost;
        foreach (OutputTitle title in output.Titles)
        {
            cost = Add(cost, ItemCost(title.Item));
        }

        return Add(cost, NodesCost(output.Items));
    }

    // The parser bounds the depth (PluginOutputParser.MaxDepth), so this recursion is bounded.
    private static long NodesCost(ImmutableArray<MenuNode> nodes)
    {
        long cost = 0;
        foreach (MenuNode node in nodes)
        {
            cost = Add(cost, node is MenuItem item ? ItemCost(item) : NodeCost);
        }

        return cost;
    }

    private static long ItemCost(MenuItem item)
    {
        long cost = Add(NodeCost, StringCostOf(item.Text));
        TextFormat format = item.Format;
        if (!ReferenceEquals(format, TextFormat.Default))
        {
            cost = Add(cost, FormatCost);
            cost = Add(cost, format.Color is null ? 0 : FormatCost);
            cost = Add(cost, StringCostOf(format.Font));
            cost = Add(cost, StringCostOf(format.Tooltip));
        }

        switch (item.Action)
        {
            case CommandAction command:
                cost = Add(cost, ActionCost);
                cost = Add(cost, StringCostOf(command.Executable));
                cost = Add(cost, ArrayCostOf(command.Arguments.Length));
                foreach (string argument in command.Arguments)
                {
                    cost = Add(cost, StringCostOf(argument));
                }

                break;

            case UrlAction url:
                cost = Add(cost, ActionCost + UriCost);
                cost = Add(cost, Multiply(StringCostOf(url.Url.OriginalString), 3));
                break;
        }

        if (!item.Children.IsEmpty)
        {
            cost = Add(cost, ArrayCostOf(item.Children.Length));
            cost = Add(cost, NodesCost(item.Children));
        }

        return cost;
    }

    private static long StringCostOf(string? value) => value is null ? 0 : Add(StringCost, Multiply(value.Length, 2));

    private static long ArrayCostOf(int length) => length == 0 ? 0 : Add(ArrayCost, Multiply(length, ReferenceCost));

    private static long Add(long left, long right) => left > long.MaxValue - right ? long.MaxValue : left + right;

    private static long Multiply(long value, long factor) => value > long.MaxValue / factor ? long.MaxValue : value * factor;
}
