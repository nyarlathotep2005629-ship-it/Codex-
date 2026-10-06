using WorkoutTracker.Application;

namespace WorkoutTracker.Application.Tests;

public sealed class RestTimerStateTests
{
    [Fact(DisplayName = "ANDROID-M1-TIMER-001 TargetEndUtc is the timer source of truth")]
    public void TimerUsesTargetUtcInsteadOfDecrementingLocalState()
    {
        var now = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
        var timer = new RestTimerState(() => now);

        timer.Start(TimeSpan.FromSeconds(90));
        now = now.AddSeconds(31);

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 1, 30, TimeSpan.Zero), timer.TargetEndUtc);
        Assert.Equal(TimeSpan.FromSeconds(59), timer.GetRemaining());
    }

    [Fact(DisplayName = "ANDROID-M1-TIMER-002 add and stop are deterministic local timer controls")]
    public void AddAndStop()
    {
        var now = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
        var timer = new RestTimerState(() => now);
        timer.Start(TimeSpan.FromSeconds(60));
        now = now.AddSeconds(10);

        timer.Add(TimeSpan.FromSeconds(15));
        Assert.Equal(TimeSpan.FromSeconds(65), timer.GetRemaining());
        timer.Stop();

        Assert.Null(timer.TargetEndUtc);
        Assert.Equal(TimeSpan.Zero, timer.GetRemaining());
    }

    [Fact(DisplayName = "ANDROID-M1-TIMER-003 elapsed TargetEndUtc reports zero without negative time")]
    public void ElapsedTimerClampsAtZero()
    {
        var now = new DateTimeOffset(2026, 9, 22, 9, 0, 0, TimeSpan.Zero);
        var timer = new RestTimerState(() => now);
        timer.Start(TimeSpan.FromSeconds(30));
        now = now.AddMinutes(1);

        Assert.False(timer.IsRunning);
        Assert.Equal(TimeSpan.Zero, timer.GetRemaining());
    }
}
