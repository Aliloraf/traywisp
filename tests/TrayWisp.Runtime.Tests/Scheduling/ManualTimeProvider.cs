namespace TrayWisp.Runtime.Tests.Scheduling;

// A controlled clock for deterministic scheduler tests. The monotonic timestamp only moves when a test advances it; the
// wall clock (UTC) moves with it and can additionally jump independently, as after a clock adjustment. Timers fire only
// from FireNext, on the calling thread, outside the provider's lock, one due time at a time.
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _now;
    private TimeSpan _utcOffset;

    public static DateTimeOffset Epoch { get; } = new(2026, 11, 27, 8, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_lock)
        {
            return _now;
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_lock)
        {
            return Epoch + TimeSpan.FromTicks(_now) + _utcOffset;
        }
    }

    public TimeSpan Elapsed => TimeSpan.FromTicks(GetTimestamp());

    // Moves the wall clock only, as when the system time is changed. Scheduling must not notice.
    public void JumpWallClock(TimeSpan offset)
    {
        lock (_lock)
        {
            _utcOffset += offset;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_lock)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    // The earliest due time of an armed timer, in ticks, or null.
    public long? NextDue()
    {
        lock (_lock)
        {
            long? next = null;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.Due is long due && (next is null || due < next))
                {
                    next = due;
                }
            }

            return next;
        }
    }

    public int ArmedTimers
    {
        get
        {
            lock (_lock)
            {
                return _timers.Count(timer => timer.Due is not null);
            }
        }
    }

    // Sets the clock to the earliest due time (never backwards) and fires every timer due then.
    public void FireNext()
    {
        List<ManualTimer> due = [];
        lock (_lock)
        {
            long? next = null;
            foreach (ManualTimer timer in _timers)
            {
                if (timer.Due is long at && (next is null || at < next))
                {
                    next = at;
                }
            }

            if (next is null)
            {
                return;
            }

            _now = Math.Max(_now, next.Value);
            foreach (ManualTimer timer in _timers)
            {
                if (timer.Due is long at && at <= _now)
                {
                    timer.Due = timer.Period is long period && period > 0 ? _now + period : null;
                    due.Add(timer);
                }
            }
        }

        foreach (ManualTimer timer in due)
        {
            timer.Fire();
        }
    }

    // Sets the clock without firing timers (used after the due timers up to a target have been fired).
    public void SetNow(long ticks)
    {
        lock (_lock)
        {
            _now = Math.Max(_now, ticks);
        }
    }

    private void Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_lock)
        {
            timer.Due = dueTime == Timeout.InfiniteTimeSpan ? null : _now + Math.Max(dueTime.Ticks, 0);
            timer.Period = period == Timeout.InfiniteTimeSpan ? null : period.Ticks;
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_lock)
        {
            _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long? Due { get; set; }

        public long? Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Change(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            owner.Remove(this);
            return ValueTask.CompletedTask;
        }
    }
}
