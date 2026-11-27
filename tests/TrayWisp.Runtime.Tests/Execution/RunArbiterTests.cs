using TrayWisp.Runtime.Execution;
using Xunit;

namespace TrayWisp.Runtime.Tests.Execution;

// The arbitration between completion, the deadline, cancellation and failures, with a manual monotonic clock.
public sealed class RunArbiterTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly ManualClock _clock = new();

    private RunArbiter Arbiter() => new(_clock, startTimestamp: _clock.Now, Timeout);

    private static void Complete(RunArbiter arbiter)
    {
        arbiter.RecordExit();
        arbiter.RecordOutputEnd();
        arbiter.RecordErrorEnd();
    }

    [Fact]
    public void CompletionBeforeTheDeadlineSucceeds()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(3));
        Complete(arbiter);

        Assert.True(arbiter.Decisive.IsCompleted);
        Assert.Equal(RunEvent.Completion, arbiter.TryDecide());
    }

    [Fact]
    public void CompletionAfterTheDeadlineIsATimeout()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(11));
        Complete(arbiter);

        Assert.Equal(RunEvent.Timeout, arbiter.TryDecide());
    }

    [Fact]
    public void ADelayedDecisionUsesTheRecordedTimes()
    {
        // Completed before the deadline, decided long after it: still a completion.
        RunArbiter early = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(9));
        Complete(early);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(RunEvent.Completion, early.TryDecide());

        // Completed after the deadline, while the supervisor had not resumed yet: a timeout, not a success.
        RunArbiter late = Arbiter();
        _clock.Advance(Timeout + TimeSpan.FromMilliseconds(1));
        Complete(late);
        Assert.Equal(RunEvent.Timeout, late.TryDecide());
    }

    [Fact]
    public void CompletionExactlyAtTheDeadlineIsATimeout()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(Timeout);
        Complete(arbiter);

        Assert.Equal(RunEvent.Timeout, arbiter.TryDecide());
    }

    [Fact]
    public void CancellationBeforeCompletionIsNotLostWhenCompletionFollows()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(1));
        arbiter.Record(RunEvent.Cancellation);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Complete(arbiter);

        Assert.Equal(RunEvent.Cancellation, arbiter.TryDecide());
    }

    [Fact]
    public void CompletionBeforeCancellationStaysACompletion()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(1));
        Complete(arbiter);
        _clock.Advance(TimeSpan.FromSeconds(1));
        arbiter.Record(RunEvent.Cancellation);

        Assert.Equal(RunEvent.Completion, arbiter.TryDecide());
    }

    [Theory]
    [InlineData("OutputLimit", "Cancellation", "OutputLimit")]
    [InlineData("Cancellation", "OutputLimit", "OutputLimit")]
    [InlineData("CaptureFailure", "Cancellation", "CaptureFailure")]
    [InlineData("OutputLimit", "CaptureFailure", "OutputLimit")]
    [InlineData("SupervisionFailure", "Cancellation", "SupervisionFailure")]
    public void SimultaneousEventsAreDecidedByAFixedPrecedence(string first, string second, string expected)
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(2));
        arbiter.Record(Enum.Parse<RunEvent>(first));
        arbiter.Record(Enum.Parse<RunEvent>(second));

        Assert.Equal(Enum.Parse<RunEvent>(expected), arbiter.TryDecide());
    }

    [Fact]
    public void SimultaneousCancellationAndCompletionNeverPublish()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(2));
        Complete(arbiter);
        arbiter.Record(RunEvent.Cancellation);

        Assert.Equal(RunEvent.Cancellation, arbiter.TryDecide());
    }

    [Fact]
    public void ACancellationAtTheDeadlineIsACancellation()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(Timeout);
        arbiter.Record(RunEvent.Cancellation);

        Assert.Equal(RunEvent.Cancellation, arbiter.TryDecide());
    }

    [Fact]
    public void EventsAfterTheDeadlineLoseToIt()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(Timeout + TimeSpan.FromSeconds(1));
        arbiter.Record(RunEvent.Cancellation);

        Assert.Equal(RunEvent.Timeout, arbiter.TryDecide());
    }

    [Fact]
    public void APartialCompletionDecidesNothing()
    {
        RunArbiter arbiter = Arbiter();
        arbiter.RecordExit();
        arbiter.RecordOutputEnd();

        Assert.False(arbiter.Decisive.IsCompleted);
        Assert.False(arbiter.CompletionRecorded);
        Assert.Null(arbiter.TryDecide());
        Assert.Equal(Timeout, arbiter.TimeUntilDeadline());

        _clock.Advance(Timeout);
        Assert.True(arbiter.TimeUntilDeadline() <= TimeSpan.Zero);
        Assert.Equal(RunEvent.Timeout, arbiter.TryDecide());
    }

    [Fact]
    public void TheDecisionIsFinal()
    {
        RunArbiter arbiter = Arbiter();
        _clock.Advance(TimeSpan.FromSeconds(1));
        Complete(arbiter);
        Assert.Equal(RunEvent.Completion, arbiter.TryDecide());

        arbiter.Record(RunEvent.OutputLimit);
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(RunEvent.Completion, arbiter.TryDecide());
        Assert.False(arbiter.IsRecorded(RunEvent.OutputLimit));
    }

    [Fact]
    public void TheDeadlineAndCompletionCannotBeRecordedDirectly()
    {
        RunArbiter arbiter = Arbiter();
        Assert.Throws<ArgumentOutOfRangeException>(() => arbiter.Record(RunEvent.Timeout));
        Assert.Throws<ArgumentOutOfRangeException>(() => arbiter.Record(RunEvent.Completion));
    }

    [Fact]
    public void TheDeadlineIsMeasuredWithTheProvidersTimestampFrequency()
    {
        var slow = new ManualClock(frequency: 1_000);
        var arbiter = new RunArbiter(slow, slow.Now, TimeSpan.FromMilliseconds(1500));
        slow.Advance(TimeSpan.FromMilliseconds(1499));
        Complete(arbiter);

        Assert.Equal(RunEvent.Completion, arbiter.TryDecide());
    }

    // A monotonic clock that only moves when told to. Timers are not used by RunArbiter.
    private sealed class ManualClock(long frequency = TimeSpan.TicksPerSecond) : TimeProvider
    {
        public long Now { get; private set; } = 1_000_000;

        public override long TimestampFrequency => frequency;

        public override long GetTimestamp() => Now;

        public void Advance(TimeSpan by) => Now += (long)((Int128)by.Ticks * frequency / TimeSpan.TicksPerSecond);
    }
}
