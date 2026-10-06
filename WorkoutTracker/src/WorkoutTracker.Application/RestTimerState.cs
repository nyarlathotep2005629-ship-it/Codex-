namespace WorkoutTracker.Application;

/// <summary>
/// Local presentation timer. The target UTC instant is the sole source of truth: remaining time
/// is always derived from it, so UI refresh frequency can never make the timer drift.
/// </summary>
public sealed class RestTimerState
{
    private readonly Func<DateTimeOffset> _utcNow;

    public RestTimerState(Func<DateTimeOffset>? utcNow = null) => _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);

    public event EventHandler? Changed;
    public DateTimeOffset? TargetEndUtc { get; private set; }
    public bool IsRunning => TargetEndUtc is not null && GetRemaining() > TimeSpan.Zero;

    public TimeSpan GetRemaining()
    {
        if (TargetEndUtc is null) return TimeSpan.Zero;
        var remaining = TargetEndUtc.Value - _utcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    public void Start(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        TargetEndUtc = _utcNow().Add(duration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Add(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        var baseline = TargetEndUtc is { } target && target > _utcNow() ? target : _utcNow();
        TargetEndUtc = baseline.Add(duration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Stop()
    {
        if (TargetEndUtc is null) return;
        TargetEndUtc = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
