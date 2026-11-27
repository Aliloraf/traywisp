using TrayWisp.Core.Execution;

namespace TrayWisp.Runtime.Execution;

// The events that can end a run. Their order is the precedence between events recorded at the same instant:
// failures first, then cancellation, then the deadline, and natural completion last, so a tie never publishes output.
internal enum RunEvent
{
    OutputLimit,
    CaptureFailure,
    SupervisionFailure,
    Cancellation,
    Timeout,
    Completion,
}

// Decides, exactly once, why a run ended. See docs/execution-engine.md ("Timeout and cancellation").
//
// Every event is recorded with the monotonic timestamp at which TrayWisp observed it, on the thread that observed it
// (a capture thread, the exit callback, the cancellation callback), not when the supervising continuation resumes.
// Completion is the moment the last of process exit, end of standard output and end of standard error was observed.
// The deadline is an event at start + timeout. The earliest event wins; equal timestamps are decided by the order of
// RunEvent. Observation can only be later than the event itself, so a late observation errs toward a timeout or a
// cancellation, never toward publishing output that was completed after the deadline.
internal sealed class RunArbiter
{
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly long _deadline;
    private readonly long?[] _recorded = new long?[Enum.GetValues<RunEvent>().Length];
    private readonly TaskCompletionSource _decisive = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long? _exited;
    private long? _outputEnded;
    private long? _errorEnded;
    private RunEvent? _decision;

    public RunArbiter(TimeProvider time, long startTimestamp, TimeSpan timeout)
    {
        _time = time;
        _deadline = startTimestamp + (long)((Int128)timeout.Ticks * time.TimestampFrequency / TimeSpan.TicksPerSecond);
    }

    // Completes when an event that can decide the run has been recorded (the deadline is not recorded; wait for it).
    public Task Decisive => _decisive.Task;

    // For tests: whether natural completion has been recorded.
    public bool CompletionRecorded
    {
        get
        {
            lock (_gate)
            {
                return _recorded[(int)RunEvent.Completion] is not null;
            }
        }
    }

    // For tests: whether the given event has been recorded.
    public bool IsRecorded(RunEvent runEvent)
    {
        lock (_gate)
        {
            return _recorded[(int)runEvent] is not null;
        }
    }

    // Records a stopping event (overflow, capture failure, supervision failure, cancellation). Only its first
    // occurrence counts, and nothing counts after the decision.
    public void Record(RunEvent runEvent)
    {
        if (runEvent is RunEvent.Timeout or RunEvent.Completion)
        {
            throw new ArgumentOutOfRangeException(nameof(runEvent), runEvent, "The deadline and completion are not recorded directly.");
        }

        lock (_gate)
        {
            RecordLocked(runEvent, _time.GetTimestamp());
        }

        _decisive.TrySetResult();
    }

    public void RecordExit() => RecordPart(ref _exited);

    public void RecordOutputEnd() => RecordPart(ref _outputEnded);

    public void RecordErrorEnd() => RecordPart(ref _errorEnded);

    // Time left until the deadline, measured now; zero or negative once it has passed.
    public TimeSpan TimeUntilDeadline() => _time.GetElapsedTime(_time.GetTimestamp(), _deadline);

    // Decides at the current time, once; later calls return the same decision. Returns null only while nothing that
    // can decide has been recorded and the deadline has not passed.
    public RunEvent? TryDecide()
    {
        lock (_gate)
        {
            if (_decision is not null)
            {
                return _decision;
            }

            long now = _time.GetTimestamp();
            RunEvent? winner = null;
            long winnerAt = 0;
            foreach (RunEvent candidate in Enum.GetValues<RunEvent>())
            {
                long? at = candidate == RunEvent.Timeout ? (now >= _deadline ? _deadline : null) : _recorded[(int)candidate];

                // Strictly earlier only: on equal timestamps the event with the higher precedence, seen first, stays.
                if (at is not null && (winner is null || at.Value < winnerAt))
                {
                    winner = candidate;
                    winnerAt = at.Value;
                }
            }

            _decision = winner;
            return winner;
        }
    }

    public static ExecutionTerminationReason ToTerminationReason(RunEvent decision) => decision switch
    {
        RunEvent.Completion => ExecutionTerminationReason.None,
        RunEvent.Timeout => ExecutionTerminationReason.Timeout,
        RunEvent.Cancellation => ExecutionTerminationReason.Cancellation,
        RunEvent.OutputLimit => ExecutionTerminationReason.OutputLimit,
        RunEvent.CaptureFailure => ExecutionTerminationReason.CaptureFailure,
        _ => ExecutionTerminationReason.SupervisionFailure,
    };

    private void RecordPart(ref long? part)
    {
        bool complete;
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            part ??= now;
            complete = _exited is not null && _outputEnded is not null && _errorEnded is not null;
            if (complete)
            {
                RecordLocked(RunEvent.Completion, now);
            }
        }

        if (complete)
        {
            _decisive.TrySetResult();
        }
    }

    private void RecordLocked(RunEvent runEvent, long now)
    {
        if (_decision is null)
        {
            _recorded[(int)runEvent] ??= now;
        }
    }
}
