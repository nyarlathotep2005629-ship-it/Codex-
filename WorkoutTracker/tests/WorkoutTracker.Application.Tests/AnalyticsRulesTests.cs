using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Application.Tests;

public sealed class AnalyticsRulesTests
{
    [Theory(DisplayName = "DSH-002 DSH-004 DSH-018 DSH-019 finite periods are calendar based and Previous has exactly equal days")]
    [InlineData(DashboardPeriod.OneMonth)]
    [InlineData(DashboardPeriod.ThreeMonths)]
    [InlineData(DashboardPeriod.SixMonths)]
    [InlineData(DashboardPeriod.OneYear)]
    public void PeriodsHaveEqualPrevious(DashboardPeriod period)
    {
        foreach (var date in new[] { new DateOnly(2024, 2, 29), new DateOnly(2025, 3, 31), new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 31) })
        {
            var range = AnalyticsPeriodCalculator.Resolve(period, date);
            Assert.NotNull(range.PreviousStart);
            Assert.Equal(range.DayCount, range.Start.DayNumber - range.PreviousStart.Value.DayNumber);
            Assert.Equal(date.AddDays(1), range.EndExclusive);
        }
    }

    [Fact(DisplayName = "DSH-005 ALL starts at oldest Completed and has no Previous")]
    public void AllUsesOldest()
    {
        var oldest = new DateOnly(2019, 5, 4); var range = AnalyticsPeriodCalculator.Resolve(DashboardPeriod.All, new(2026, 8, 22), oldest);
        Assert.Equal(oldest, range.Start); Assert.Equal(new DateOnly(2026, 8, 23), range.EndExclusive); Assert.Null(range.PreviousStart);
    }

    [Fact(DisplayName = "DSH-003 DSH-007 comparison handles no baseline exact difference and percentage")]
    public void ComparisonRules()
    {
        var none = AnalyticsMath.Compare("Volume", 100m, 0m, false); Assert.Equal(ComparisonStatus.NoBaseline, none.Status); Assert.Null(none.PercentageDifference);
        var increased = AnalyticsMath.Compare("Volume", 125m, 100m, false); Assert.Equal(25m, increased.AbsoluteDifference); Assert.Equal(25m, increased.PercentageDifference); Assert.Equal(ComparisonStatus.Increased, increased.Status);
        var all = AnalyticsMath.Compare("Volume", 125m, 100m, true); Assert.Equal(ComparisonStatus.NotApplicable, all.Status); Assert.Null(all.PercentageDifference);
    }

    [Theory(DisplayName = "STA-001 1RM accepts reps 1 and 15 with unrounded decimal and rejects 16")]
    [InlineData(1, "103.33333333333333333333333333")]
    [InlineData(15, "150")]
    public void EpleyBoundaries(int reps, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), AnalyticsMath.EstimatedOneRepMax(10000, reps));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnalyticsMath.EstimatedOneRepMax(10000, 16));
    }

    [Fact(DisplayName = "DSH-009 Dashboard selection is limited to four exercises")]
    public async Task DashboardSelectionMaximum()
    {
        var service = new AnalyticsService(new EmptyQuery(), new InlineExecutor());
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SaveDashboardSettingsAsync(DashboardPeriod.ThreeMonths, Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToList()));
    }

    [Fact(DisplayName = "ASY-003 off-UI executor performs analytics work on a worker thread while caller remains responsive")]
    public async Task OffUiBoundary()
    {
        var executor = new TaskSchedulerOffUiExecutor(); var caller = Environment.CurrentManagedThreadId; var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = executor.RunAsync(_ => { var workerThread = Environment.CurrentManagedThreadId; started.SetResult(); var until = System.Diagnostics.Stopwatch.StartNew(); while (until.Elapsed < TimeSpan.FromMilliseconds(350)) Thread.SpinWait(20_000); return Task.FromResult(workerThread); }); await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var heartbeat = new List<double>(); var timer = System.Diagnostics.Stopwatch.StartNew(); var previous = timer.Elapsed;
        while (!work.IsCompleted) { await Task.Delay(20); var now = timer.Elapsed; heartbeat.Add((now - previous).TotalMilliseconds); previous = now; }
        var worker = await work; var ordered = heartbeat.OrderBy(x => x).ToList(); var p95 = ordered[(int)Math.Ceiling(ordered.Count * 0.95) - 1];
        Assert.NotEqual(caller, worker); Assert.True(p95 <= 100, $"heartbeat p95={p95:0.0}ms"); Assert.True(heartbeat.Max() <= 250, $"heartbeat max={heartbeat.Max():0.0}ms");
    }

    private sealed class InlineExecutor : IOffUiExecutor
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class EmptyQuery : IAnalyticsQuery
    {
        public Task<DateOnly?> GetOldestCompletedWorkoutDateAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateOnly?>(null);
        public Task<IReadOnlyList<ExerciseOptionDto>> GetExerciseOptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExerciseOptionDto>>([]);
        public Task<AnalyticsSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AnalyticsSettings(DashboardPeriod.ThreeMonths, []));
        public Task SaveDashboardSettingsAsync(AnalyticsSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DashboardDto> GetDashboardAsync(DashboardReadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<StatisticsDto> GetStatisticsAsync(StatisticsReadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
