namespace Moodle.Application.Tests.Infrastructure;

internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider, IDisposable
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _utcNow = start;
    private long _timestamp;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _utcNow;
    }

    public override long GetTimestamp()
    {
        lock (_gate) return _timestamp;
    }

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        lock (_gate) _timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));

        List<(TimerCallback Callback, object? State)> callbacks = [];
        lock (_gate)
        {
            _utcNow += duration;
            _timestamp += duration.Ticks;
            foreach (var timer in _timers.ToArray())
            {
                if (timer.PrepareFire(_timestamp, out var callback, out var state))
                    callbacks.Add((callback, state));
            }
        }

        foreach (var (callback, state) in callbacks) callback(state);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var timer in _timers.ToArray()) timer.DisposeLocked();
            _timers.Clear();
        }
    }

    private bool ChangeTimer(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        ValidateTimeout(dueTime, nameof(dueTime));
        ValidateTimeout(period, nameof(period));
        lock (_gate)
        {
            if (timer.IsDisposed) return false;
            timer.DueTimestamp = dueTime == Timeout.InfiniteTimeSpan ? null : _timestamp + dueTime.Ticks;
            timer.Period = period;
            return true;
        }
    }

    private void RemoveTimer(ManualTimer timer)
    {
        lock (_gate)
        {
            timer.DisposeLocked();
            _timers.Remove(timer);
        }
    }

    private static void ValidateTimeout(TimeSpan value, string parameterName)
    {
        if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(parameterName);
    }

    private sealed class ManualTimer(
        ManualTimeProvider owner,
        TimerCallback callback,
        object? state) : ITimer
    {
        public long? DueTimestamp { get; set; }
        public TimeSpan Period { get; set; } = Timeout.InfiniteTimeSpan;
        public bool IsDisposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => owner.ChangeTimer(this, dueTime, period);

        public void Dispose() => owner.RemoveTimer(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }

        public bool PrepareFire(long now, out TimerCallback dueCallback, out object? dueState)
        {
            dueCallback = callback;
            dueState = state;
            if (IsDisposed || DueTimestamp is not { } due || due > now) return false;
            DueTimestamp = Period == Timeout.InfiniteTimeSpan
                ? null
                : now + Math.Max(Period.Ticks, 1);
            return true;
        }

        public void DisposeLocked()
        {
            IsDisposed = true;
            DueTimestamp = null;
        }
    }
}
