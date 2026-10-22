namespace SocialTelemetry.Tests.Features.Profiles;

// Test-only controllable clock/timers. Advancing it runs due callbacks without sleeps.
internal sealed class LocalAccessClock : TimeProvider
{
    private readonly object gate = new();
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    private readonly List<ClockTimer> timers = [];
    public TaskCompletionSource FirstTimer { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override DateTimeOffset GetUtcNow() { lock (gate) return now; }
    public override long GetTimestamp() => GetUtcNow().UtcTicks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public void Advance(TimeSpan amount)
    {
        ClockTimer[] due;
        lock (gate)
        {
            now += amount;
            due = timers.Where(timer => timer.Due <= now).ToArray();
            foreach (var timer in due) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + timer.Period;
        }
        foreach (var timer in due) timer.Invoke();
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (gate)
        {
            var timer = new ClockTimer(this, callback, state);
            timer.Change(dueTime, period);
            timers.Add(timer);
            FirstTimer.TrySetResult();
            return timer;
        }
    }

    private sealed class ClockTimer(LocalAccessClock owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset Due { get; set; } = DateTimeOffset.MaxValue;
        public TimeSpan Period { get; private set; }
        private bool disposed;
        public void Invoke() { if (!disposed) callback(state); }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                if (disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : owner.now + dueTime;
                Period = period;
                return true;
            }
        }
        public void Dispose() { lock (owner.gate) { disposed = true; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
