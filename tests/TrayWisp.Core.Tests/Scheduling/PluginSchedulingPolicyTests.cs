using TrayWisp.Core.Execution;
using TrayWisp.Core.Output;
using TrayWisp.Core.Plugins;
using TrayWisp.Core.Scheduling;
using TrayWisp.Core.Trust;
using Xunit;

namespace TrayWisp.Core.Tests.Scheduling;

public sealed class PluginSchedulingPolicyTests
{
    private static RefreshInterval Interval(long value, RefreshIntervalUnit unit)
    {
        Assert.True(RefreshInterval.TryCreate(value, unit, out RefreshInterval? interval));
        return interval;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(4, 40)]
    [InlineData(5, 60)]
    [InlineData(6, 60)]
    [InlineData(1_000, 60)]
    [InlineData(int.MaxValue, 60)]
    public void TheFailureBackoffDoublesFrom5SecondsUpTo60(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), PluginSchedulingPolicy.GetFailureBackoff(failures));

    [Fact]
    public void NegativeFailureCountsAreRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => PluginSchedulingPolicy.GetFailureBackoff(-1));

    [Fact]
    public void AutomaticDelaysUseFixedDelayAndNeverRetryFasterThanTheInterval()
    {
        Assert.Null(PluginSchedulingPolicy.GetNextAutomaticDelay(null, succeeded: true, consecutiveFailures: 0));
        Assert.Null(PluginSchedulingPolicy.GetNextAutomaticDelay(null, succeeded: false, consecutiveFailures: 3));

        RefreshInterval fast = Interval(250, RefreshIntervalUnit.Milliseconds);
        Assert.Equal(TimeSpan.FromMilliseconds(250), PluginSchedulingPolicy.GetNextAutomaticDelay(fast, succeeded: true, consecutiveFailures: 0));
        Assert.Equal(TimeSpan.FromSeconds(5), PluginSchedulingPolicy.GetNextAutomaticDelay(fast, succeeded: false, consecutiveFailures: 1));
        Assert.Equal(TimeSpan.FromSeconds(60), PluginSchedulingPolicy.GetNextAutomaticDelay(fast, succeeded: false, consecutiveFailures: 9));

        // A failure counted as zero still waits at least the first backoff.
        Assert.Equal(TimeSpan.FromSeconds(5), PluginSchedulingPolicy.GetNextAutomaticDelay(fast, succeeded: false, consecutiveFailures: 0));

        RefreshInterval slow = Interval(2, RefreshIntervalUnit.Minutes);
        Assert.Equal(TimeSpan.FromMinutes(2), PluginSchedulingPolicy.GetNextAutomaticDelay(slow, succeeded: false, consecutiveFailures: 9));
        RefreshInterval longest = Interval(30, RefreshIntervalUnit.Days);
        Assert.Equal(TimeSpan.FromDays(30), PluginSchedulingPolicy.GetNextAutomaticDelay(longest, succeeded: false, consecutiveFailures: 1));
        Assert.Equal(TimeSpan.FromDays(30), PluginSchedulingPolicy.GetNextAutomaticDelay(longest, succeeded: true, consecutiveFailures: 0));
    }

    [Fact]
    public void TheMinimumRelaunchDelayIsTheShortestInterval() =>
        Assert.Equal(RefreshInterval.Minimum, PluginSchedulingPolicy.MinimumRelaunchDelay);

    [Fact]
    public void EligibilityFollowsItsPrecedence()
    {
        var eligible = new PluginEligibilityFacts(false, true, true, PluginPresence.Present, false, PluginTrustStatus.Approved);
        Assert.Equal(PluginEligibility.Eligible, PluginSchedulingPolicy.EvaluateEligibility(eligible));
        Assert.Equal(PluginEligibility.CleanupBlocked, PluginSchedulingPolicy.EvaluateEligibility(new PluginEligibilityFacts(true, false, false, PluginPresence.Uncertain, true, null)));
        Assert.Equal(PluginEligibility.UnsupportedLanguage, PluginSchedulingPolicy.EvaluateEligibility(eligible with { LanguageSupported = false, Enabled = false }));
        Assert.Equal(PluginEligibility.Disabled, PluginSchedulingPolicy.EvaluateEligibility(eligible with { Enabled = false, Presence = PluginPresence.Uncertain }));
        Assert.Equal(PluginEligibility.Uncertain, PluginSchedulingPolicy.EvaluateEligibility(eligible with { Presence = PluginPresence.Uncertain, AwaitingTrustCheck = true }));
        Assert.Equal(PluginEligibility.AwaitingTrustCheck, PluginSchedulingPolicy.EvaluateEligibility(eligible with { AwaitingTrustCheck = true }));
        Assert.Equal(PluginEligibility.AwaitingTrustCheck, PluginSchedulingPolicy.EvaluateEligibility(eligible with { ObservedTrust = null }));

        // A cached Approved is required, and nothing else counts as approved.
        foreach (PluginTrustStatus status in Enum.GetValues<PluginTrustStatus>())
        {
            PluginEligibility expected = status switch
            {
                PluginTrustStatus.Approved => PluginEligibility.Eligible,
                PluginTrustStatus.PendingApproval or PluginTrustStatus.ContentChanged or PluginTrustStatus.StaleApproval => PluginEligibility.PendingApproval,
                _ => PluginEligibility.Unavailable,
            };
            Assert.Equal(expected, PluginSchedulingPolicy.EvaluateEligibility(eligible with { ObservedTrust = status }));
        }
    }

    [Fact]
    public void TheStatusNeverHidesAReasonThatPreventsLaunching()
    {
        foreach (PluginEligibility eligibility in Enum.GetValues<PluginEligibility>().Where(value => value != PluginEligibility.Eligible))
        {
            foreach (PluginActivity activity in Enum.GetValues<PluginActivity>())
            {
                PluginScheduleStatus status = PluginSchedulingPolicy.GetStatus(eligibility, activity, PluginRunDisposition.Published);
                Assert.Equal(eligibility.ToString(), status.ToString());
            }
        }

        Assert.Equal(PluginScheduleStatus.Running, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.Running, PluginRunDisposition.Failed));
        Assert.Equal(PluginScheduleStatus.Running, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.Cancelling, null));
        Assert.Equal(PluginScheduleStatus.Queued, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.Queued, PluginRunDisposition.Failed));
        Assert.Equal(PluginScheduleStatus.Idle, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.None, null));
        Assert.Equal(PluginScheduleStatus.Idle, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.None, PluginRunDisposition.Published));
        Assert.Equal(PluginScheduleStatus.Idle, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.None, PluginRunDisposition.Superseded));
        Assert.Equal(PluginScheduleStatus.Failed, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.None, PluginRunDisposition.Failed));
        Assert.Equal(PluginScheduleStatus.Failed, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.None, PluginRunDisposition.NotPublished));
        Assert.Equal(PluginScheduleStatus.Failed, PluginSchedulingPolicy.GetStatus(PluginEligibility.Eligible, PluginActivity.None, PluginRunDisposition.Faulted));
    }

    [Fact]
    public void ManualRefreshRejectionsMatchEligibility()
    {
        Assert.Null(PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.Eligible));
        Assert.Null(PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.AwaitingTrustCheck));
        Assert.Equal(RefreshRequestStatus.CleanupBlocked, PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.CleanupBlocked));
        Assert.Equal(RefreshRequestStatus.UnsupportedLanguage, PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.UnsupportedLanguage));
        Assert.Equal(RefreshRequestStatus.Disabled, PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.Disabled));
        Assert.Equal(RefreshRequestStatus.Uncertain, PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.Uncertain));
        Assert.Equal(RefreshRequestStatus.NotApproved, PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.PendingApproval));
        Assert.Equal(RefreshRequestStatus.Unavailable, PluginSchedulingPolicy.GetManualRefreshRejection(PluginEligibility.Unavailable));
    }

    [Fact]
    public void EitherIncompleteCleanupSignalBlocks()
    {
        Assert.True(PluginSchedulingPolicy.IsCleanupUnresolved(PluginExecutionStatus.CleanupIncomplete, ProcessCleanupStatus.Incomplete));
        Assert.True(PluginSchedulingPolicy.IsCleanupUnresolved(PluginExecutionStatus.CleanupIncomplete, ProcessCleanupStatus.Terminated));
        Assert.True(PluginSchedulingPolicy.IsCleanupUnresolved(PluginExecutionStatus.TimedOut, ProcessCleanupStatus.Incomplete));
        Assert.False(PluginSchedulingPolicy.IsCleanupUnresolved(PluginExecutionStatus.TimedOut, ProcessCleanupStatus.Terminated));
        Assert.False(PluginSchedulingPolicy.IsCleanupUnresolved(PluginExecutionStatus.Succeeded, ProcessCleanupStatus.DescendantsTerminated));
        Assert.False(PluginSchedulingPolicy.IsCleanupUnresolved(PluginExecutionStatus.TrustDenied, ProcessCleanupStatus.NotStarted));
    }

    [Fact]
    public void TheOutputCostCountsNodesStringsAndCollections()
    {
        // Empty output: the fixed part only, so complete empty output stays publishable.
        PluginOutput empty = PluginOutputParser.Parse(string.Empty).Output;
        Assert.Equal(1024, PluginSchedulingPolicy.EstimateRetainedOutputCost(empty));
        Assert.Throws<ArgumentNullException>(() => PluginSchedulingPolicy.EstimateRetainedOutputCost(null!));

        // Per node 128, per string 32 + 2 per code unit, per non-empty submenu 32 + 8 per child, separators 128.
        PluginOutput nested = PluginOutputParser.Parse("title\n---\na\n--b\n----c\n---\nd").Output;
        long title = 128 + (32 + 10);
        long c = 128 + 34;
        long b = 128 + 34 + (32 + 8) + c;
        long a = 128 + 34 + (32 + 8) + b;
        long separator = 128;
        long d = 128 + 34;
        Assert.Equal(1024 + title + a + separator + d, PluginSchedulingPolicy.EstimateRetainedOutputCost(nested));
    }

    [Fact]
    public void TheOutputCostCountsCommandArgumentsActionsAndFormats()
    {
        string arguments = string.Join(' ', Enumerable.Range(1, PluginOutputParser.MaxCommandArguments).Select(i => $"param{i}=aa"));
        PluginOutput command = PluginOutputParser.Parse($"t\n---\nx | shell=e {arguments}").Output;
        MenuItem item = Assert.IsType<MenuItem>(Assert.Single(command.Items));
        Assert.Equal(32, Assert.IsType<CommandAction>(item.Action).Arguments.Length);

        // The node, its label ("x ": labels keep their white space), the action, the executable, the argument array and
        // 32 argument strings.
        Assert.Equal("x ", item.Text);
        long label = 32 + (2 * 2);
        long node = 128 + label + 48 + 34 + (32 + (32 * 8)) + (32 * (32 + 4));
        Assert.Equal(1024 + (128 + 34) + node, PluginSchedulingPolicy.EstimateRetainedOutputCost(command));

        // A URL counts its object and three copies of its text; formatting and a colour count their records and strings.
        PluginOutput url = PluginOutputParser.Parse("t\n---\nx | href=https://example.com/").Output;
        Assert.Equal(1024 + (128 + 34) + (128 + label + 48 + 192 + (3 * (32 + 40))), PluginSchedulingPolicy.EstimateRetainedOutputCost(url));
        PluginOutput formatted = PluginOutputParser.Parse("t | color=red font=Mono tooltip=hi").Output;
        Assert.Equal(1024 + (128 + label + 96 + 96 + (32 + 8) + (32 + 4)), PluginSchedulingPolicy.EstimateRetainedOutputCost(formatted));

        // The shared refresh action costs nothing.
        PluginOutput refresh = PluginOutputParser.Parse("t\n---\nx | refresh=true").Output;
        Assert.Equal(1024 + (128 + 34) + (128 + label), PluginSchedulingPolicy.EstimateRetainedOutputCost(refresh));
    }

    [Fact]
    public void ArgumentHeavyMenusCostMoreThanTheirTextAndStayBounded()
    {
        // The largest argument-heavy menu the parser accepts: as many 32-argument commands as fit in its input limit.
        string arguments = string.Join(' ', Enumerable.Range(1, PluginOutputParser.MaxCommandArguments).Select(i => $"param{i}=a"));
        string line = $"x | shell=e {arguments}\n";
        int lines = Math.Min((PluginOutputParser.MaxInputLength - 8) / line.Length, PluginOutputParser.MaxMenuNodes);
        string text = "t\n---\n" + string.Concat(Enumerable.Repeat(line, lines));
        PluginOutputParseResult parsed = PluginOutputParser.Parse(text);
        Assert.Equal(lines, parsed.Output.Items.Length);

        long cost = PluginSchedulingPolicy.EstimateRetainedOutputCost(parsed.Output);

        // At least the argument strings' own overhead, which the earlier per-node estimate (512) did not cover.
        Assert.True(cost >= (long)lines * 32 * (32 + 2), $"{cost}");
        Assert.True(cost > 1024 + (2L * text.Length) + (512L * (lines + 1)), $"{cost}");

        // Bounded: the 32 MiB aggregate budget still admits more than ten of the largest such outputs.
        Assert.True(cost < 3L * 1024 * 1024, $"{cost}");
    }

    [Fact]
    public void DeeperNestingCostsOneNodeStringAndArrayPerLevel()
    {
        long previous = PluginSchedulingPolicy.EstimateRetainedOutputCost(PluginOutputParser.Parse("t\n---\nx").Output);
        for (int depth = 1; depth <= PluginOutputParser.MaxDepth; depth++)
        {
            string text = "t\n---\nx" + string.Concat(Enumerable.Range(1, depth).Select(level => "\n" + new string('-', 2 * level) + "x"));
            PluginOutput output = PluginOutputParser.Parse(text).Output;
            long cost = PluginSchedulingPolicy.EstimateRetainedOutputCost(output);
            Assert.Equal(previous + 128 + 34 + (32 + 8), cost);
            previous = cost;
        }
    }

    [Fact]
    public void SchedulerDiagnosticCodesStayInTheirRange()
    {
        Assert.All(Enum.GetValues<SchedulerDiagnosticCode>(), code => Assert.InRange((int)code, 700, 799));
        Assert.Equal(Enum.GetValues<SchedulerDiagnosticCode>().Length, Enum.GetValues<SchedulerDiagnosticCode>().Select(code => (int)code).Distinct().Count());
        Assert.Equal("Error TW720: x", new SchedulerDiagnostic(PluginDiagnosticSeverity.Error, SchedulerDiagnosticCode.CleanupIncomplete, "x").ToString());
    }
}
