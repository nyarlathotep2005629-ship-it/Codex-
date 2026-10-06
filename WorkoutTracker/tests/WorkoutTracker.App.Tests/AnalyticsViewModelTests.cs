using WorkoutTracker.Application;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Views;
using WorkoutTracker.Domain;

namespace WorkoutTracker.App.Tests;

public sealed class AnalyticsViewModelTests
{
    [Fact(DisplayName = "NFR-005 Chart adapter handles 0 1 N points labels units and independent Small Multiples Y ranges")]
    public void ChartPlans()
    {
        var empty = ScottPlotChartAdapter.CreatePlan(new(Guid.NewGuid(), "Empty", "kg", [])); Assert.True(empty.IsEmpty);
        var a = Series("Heavy", 100m, 200m); var b = Series("Light", 5m, 10m); var one = ScottPlotChartAdapter.CreatePlan(new(Guid.NewGuid(), "One", "kg", [new(new(2026, 1, 1), 50m)]));
        var plans = ScottPlotChartAdapter.CreateSmallMultiples([a, b]); var plots = new[] { new ScottPlot.Plot(), new ScottPlot.Plot() };
        Assert.Equal(2, ScottPlotChartAdapter.Render(plots[0], a)); Assert.Equal(2, ScottPlotChartAdapter.Render(plots[1], b));
        Assert.Single(one.X); Assert.Equal("kg", one.Unit); Assert.Equal(2, plans.Count); Assert.Equal(100d, plans[0].MinimumY); Assert.Equal(5d, plans[1].MinimumY); Assert.NotSame(plans[0].Y, plans[1].Y);
    }

    [Fact(DisplayName = "ASY-010 NFR-005 ScottPlot adapter renders four by 2000 points within target and keeps plots independent")]
    public void ScottPlotFourByTwoThousand()
    {
        var series = Enumerable.Range(0, 4).Select(index => Series($"E{index}", Enumerable.Range(0, 2000).Select(point => (decimal)(index * 1000 + point % 100)).ToArray())).ToList();
        var plots = series.Select(_ => new ScottPlot.Plot()).ToList(); var sw = System.Diagnostics.Stopwatch.StartNew(); for (var i = 0; i < 4; i++) Assert.Equal(2000, ScottPlotChartAdapter.Render(plots[i], series[i])); sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromMilliseconds(500), $"Elapsed {sw.Elapsed}"); Assert.Equal(4, plots.Distinct().Count());
    }

    [Fact(DisplayName = "ASY-001 ASY-002 period change cancels old request and stale result cannot overwrite latest UI")]
    public async Task LatestDashboardRequestWins()
    {
        var query = new SequencedQuery(); var vm = new DashboardViewModel(new(query, new InlineExecutor())); await vm.InitializeAsync();
        var old = vm.RefreshAsync(); await query.OldStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)); var latest = vm.RefreshAsync(); await latest; query.ReleaseOld.SetResult(); await old;
        Assert.Equal(3, vm.Data!.Summary.WorkoutCount); Assert.True(query.CancellationObserved);
    }

    [Fact(DisplayName = "STA-005 STA-006 STA-007 Statistics metric exercise and period bindings show latest DTO")]
    public async Task StatisticsInitializesAllSelectors()
    {
        var query = new SequencedQuery(); var vm = new StatisticsViewModel(new(query, new InlineExecutor())); await vm.InitializeAsync();
        Assert.Single(vm.ExerciseOptions); Assert.Equal(5, vm.MetricOptions.Count); Assert.Equal(5, vm.PeriodOptions.Count); Assert.NotNull(vm.Series); Assert.Equal("Loaded", vm.PageState);
    }

    [Fact(DisplayName = "UI-003 Workout Frequency hides zero-only rows and keeps a compact scale")]
    public async Task WorkoutFrequencyIsCompact()
    {
        var vm = new DashboardViewModel(new(new SequencedQuery(), new InlineExecutor()));
        await vm.InitializeAsync();

        Assert.Equal([2, 1], vm.CompactFrequency.Select(x => x.WorkoutCount));
        Assert.Equal(2, vm.MaxFrequencyWorkoutCount);
        Assert.True(vm.HasFrequencyData);
    }

    private static ChartSeriesDto Series(string name, params decimal[] values) => new(Guid.NewGuid(), name, "kg", values.Select((value, index) => new ChartPointDto(new DateOnly(2026, 1, 1).AddDays(index), value, name, Tooltip: $"{name} {value}")).ToList());
    private sealed class InlineExecutor : IOffUiExecutor
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class SequencedQuery : IAnalyticsQuery
    {
        private int _dashboardCalls;
        public TaskCompletionSource OldStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }
        public Task<DateOnly?> GetOldestCompletedWorkoutDateAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateOnly?>(new DateOnly(2026, 1, 1));
        public Task<IReadOnlyList<ExerciseOptionDto>> GetExerciseOptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExerciseOptionDto>>([new(Guid.Parse("00000000-0000-0000-0000-000000000001"), "Bench")]);
        public Task<AnalyticsSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AnalyticsSettings(DashboardPeriod.ThreeMonths, []));
        public Task SaveDashboardSettingsAsync(AnalyticsSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public async Task<DashboardDto> GetDashboardAsync(DashboardReadRequest request, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _dashboardCalls);
            if (call == 2)
            {
                OldStarted.SetResult(); using var registration = cancellationToken.Register(() => CancellationObserved = true); await ReleaseOld.Task;
            }
            var summary = new DashboardSummaryDto(request.SelectedPeriod, call, call * 100m, call, []);
            var start = request.Period.Start;
            IReadOnlyList<WorkoutFrequencyPointDto> frequency =
            [
                new(start, start.AddDays(7), 0),
                new(start.AddDays(7), start.AddDays(14), 2),
                new(start.AddDays(14), start.AddDays(21), 0),
                new(start.AddDays(21), start.AddDays(28), 1)
            ];
            return new(request.Period, summary, [], frequency, [], [], [], []);
        }
        public Task<StatisticsDto> GetStatisticsAsync(StatisticsReadRequest request, CancellationToken cancellationToken = default)
        {
            var series = new ChartSeriesDto(request.ExerciseId, "Bench", "kg", [new(new(2026, 8, 1), 100m)]);
            return Task.FromResult(new StatisticsDto(request.Period, request.ExerciseId, "Bench", request.Metric, 100m, 100m, Guid.NewGuid(), 80m, 1, series));
        }
    }
}
