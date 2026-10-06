using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.Application;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Views;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.App.Tests;

public sealed class HumanAcceptanceRound3Tests
{
    [Fact(DisplayName = "E2E-001 Real SQLite master-to-workout-to-analytics-to-restart flow")]
    public async Task E2e001RealDataFlowsAcrossTheApplication()
    {
        var root = Path.Combine(Path.GetTempPath(), "WorkoutTracker.Round3", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "round3.db");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var firstDate = today.AddDays(-1);
        var firstCompletedAt = new DateTimeOffset(today.ToDateTime(new TimeOnly(10, 0)), TimeZoneInfo.Local.GetUtcOffset(today.ToDateTime(new TimeOnly(10, 0)))).ToUniversalTime();
        var clock = new MutableClock(firstCompletedAt.AddHours(-2));
        Guid chestId;
        Guid exerciseId;
        Guid menuId;
        Guid firstSessionId;

        try
        {
            await using (var first = new Round3Host(databasePath, clock))
            {
                await first.InitializeAsync();
                await first.Settings.SaveThemeAsync(ThemeMode.Dark);

                var chest = await first.MasterData.SaveMuscleGroupAsync(new(null, "胸", 0));
                var shoulder = await first.MasterData.SaveMuscleGroupAsync(new(null, "肩", 1));
                var triceps = await first.MasterData.SaveMuscleGroupAsync(new(null, "三頭", 2));
                var exercise = await first.MasterData.SaveExerciseAsync(new(null, "ベンチプレス", chest.Id, [shoulder.Id, triceps.Id], "Round 3 E2E"));
                var menu = await first.MasterData.SaveMenuAsync(new(null, "胸の日", "Round 3 E2E menu", [new(null, exercise.Id, 3, "Main lift")]));
                chestId = chest.Id;
                exerciseId = exercise.Id;
                menuId = menu.Id;

                Assert.Equal(3, (await first.MasterData.GetMuscleGroupsAsync()).Count);
                Assert.Equal([shoulder.Id, triceps.Id], exercise.SecondaryMuscleIds);
                Assert.Equal(3, menu.PlannedSetCount);

                var started = Assert.IsType<StartWorkoutResult.Started>(await first.Workout.StartAsync(menu.Id, firstDate));
                firstSessionId = started.Workout.SessionId;
                Assert.Equal(menu.Id, started.Workout.SourceMenuId);
                Assert.Equal("胸の日", started.Workout.WorkoutName);
                var workoutExercise = Assert.Single(started.Workout.Exercises);
                Assert.Equal("ベンチプレス", workoutExercise.ExerciseName);
                Assert.Equal(chest.Id, workoutExercise.PrimaryMuscleId);
                Assert.Equal("胸", workoutExercise.PrimaryMuscleName);
                Assert.Equal(3, workoutExercise.Sets.Count);
                Assert.True(first.State.IsActive);

                await AssertSingleDraftAsync(first, firstSessionId, menu.Id, "ベンチプレス", "胸");

                var exerciseVm = Assert.Single(first.Workout.Exercises);
                Set(exerciseVm.Sets[0], 100m, 5, 8m);
                Set(exerciseVm.Sets[1], 100m, 5, 8.5m);
                Set(exerciseVm.Sets[2], 95m, 8, 9m);

                Assert.Equal(9, first.State.EditRevision);
                Assert.Equal(0, first.State.LastSavedRevision);
                Assert.Equal(WorkoutSaveState.Dirty, first.State.SaveState);
                Assert.All(first.Delay.RequestedDelays, delay => Assert.Equal(AutoSaveCoordinator.DebounceInterval, delay));
                first.Delay.ReleaseLatest();
                await WaitUntilAsync(() => first.State.SaveState == WorkoutSaveState.Saved && first.AutoSave.IsQueueEmpty);

                Assert.Equal(9, first.State.LastSavedRevision);
                Assert.Equal(first.State.EditRevision, first.State.LastSavedRevision);
                Assert.Equal(WorkoutSaveState.Saved, first.State.SaveState);
                await AssertSavedSetsAsync(first, firstSessionId, [(100m, 5, 8m), (100m, 5, 8.5m), (95m, 8, 9m)]);

                var sessionBeforeNavigation = first.State.SessionId;
                var revisionBeforeNavigation = first.State.EditRevision;
                var draftHistory = await first.History.GetHistoryPageAsync(HistoryQuery(50));
                var draftCalendar = await first.History.GetCalendarMonthAsync(firstDate.Year, firstDate.Month);
                var draftDashboard = await first.Analytics.GetDashboardAsync(DashboardPeriod.ThreeMonths, today, [exercise.Id]);
                var draftStatistics = await first.Analytics.GetStatisticsAsync(exercise.Id, DashboardPeriod.ThreeMonths, StatisticsMetric.Volume, today);
                await first.DashboardViewModel.InitializeAsync();
                await first.StatisticsViewModel.InitializeAsync();

                Assert.Empty(draftHistory.Items);
                Assert.Equal(0, draftCalendar.Days.Sum(x => x.WorkoutCount));
                Assert.Equal(0, draftDashboard.Summary.WorkoutCount);
                Assert.Equal(0, draftStatistics.WorkoutCount);
                Assert.Equal(sessionBeforeNavigation, first.State.SessionId);
                Assert.Equal(revisionBeforeNavigation, first.State.EditRevision);
                Assert.Equal(revisionBeforeNavigation, first.State.LastSavedRevision);
                Assert.Equal(WorkoutSaveState.Saved, first.State.SaveState);
                Assert.True(first.State.IsActive);
                await AssertDraftCountAsync(first, 1);
            }

            clock.UtcNow = firstCompletedAt;
            await using (var second = new Round3Host(databasePath, clock))
            {
                await second.InitializeAsync();
                await second.Workout.InitializeAsync();

                Assert.Equal(firstSessionId, second.Workout.SessionId);
                Assert.True(second.State.IsActive);
                Assert.Equal(ActiveWorkoutLifecycle.ActiveDraft, second.State.Lifecycle);
                Assert.Equal(0, second.State.EditRevision);
                Assert.Equal(0, second.State.LastSavedRevision);
                Assert.Equal(WorkoutSaveState.Saved, second.State.SaveState);
                Assert.Equal([(100m, 5, 8m), (100m, 5, 8.5m), (95m, 8, 9m)], Values(second.Workout));
                await AssertDraftCountAsync(second, 1);

                await second.Workout.FinishAsync();
                Assert.Null(second.Workout.LastOperationError);
                Assert.False(second.State.IsActive);
                Assert.Equal(ActiveWorkoutLifecycle.NoDraft, second.State.Lifecycle);
                await AssertDraftCountAsync(second, 0);

                await using (var db = await second.Factory.CreateDbContextAsync())
                {
                    var completed = await db.WorkoutSessions.AsNoTracking().SingleAsync(x => x.Id == firstSessionId);
                    Assert.Equal(WorkoutStatus.Completed, completed.Status);
                    Assert.Equal(firstCompletedAt, completed.CompletedAtUtc);
                }

                var history = await second.History.GetHistoryPageAsync(HistoryQuery(50));
                var item = Assert.Single(history.Items);
                Assert.Equal(firstDate, item.WorkoutDate);
                Assert.Equal(menuId, item.SourceMenuId);
                Assert.Equal("胸の日", item.SourceMenuName);
                Assert.Equal(1760m, item.TotalVolume);
                Assert.Equal(3, item.TotalSets);
                Assert.Equal(1, item.ExerciseCount);
                Assert.Null(history.NextCursor);

                var detail = await second.History.GetWorkoutDetailAsync(firstSessionId);
                Assert.NotNull(detail);
                Assert.Equal("胸の日", detail.SourceMenuName);
                Assert.Equal(1760m, detail.TotalVolume);
                Assert.Equal(3, detail.TotalSets);
                Assert.Equal("ベンチプレス", Assert.Single(detail.Exercises).ExerciseNameSnapshot);
                Assert.Equal("胸", detail.Exercises[0].PrimaryMuscleNameSnapshot);
                Assert.Equal("Main lift", detail.Exercises[0].Note);
                Assert.Equal([(100m, 5, 8m), (100m, 5, 8.5m), (95m, 8, 9m)], detail.Exercises[0].Sets.Select(x => (x.WeightKg!.Value, x.Reps!.Value, x.Rpe!.Value)).ToArray());

                var filters = await second.History.GetHistoryFilterOptionsAsync();
                Assert.Contains(filters.Exercises, x => x.Id == exerciseId && x.Name == "ベンチプレス");
                Assert.Contains(filters.PrimaryMuscles, x => x.Id == chestId && x.Name == "胸");
                Assert.Contains(filters.Menus, x => x.Id == menuId && x.Name == "胸の日");
                var filtered = await second.History.GetHistoryPageAsync(new(firstDate, today.AddDays(1), exerciseId, chestId, menuId, 25, null));
                Assert.Equal(firstSessionId, Assert.Single(filtered.Items).Id);

                var month = await second.History.GetCalendarMonthAsync(firstDate.Year, firstDate.Month);
                var day = month.Days.Single(x => x.Date == firstDate);
                var calendarWorkout = Assert.Single(day.Workouts);
                Assert.Equal(firstSessionId, calendarWorkout.WorkoutSessionId);
                Assert.Equal("胸の日", calendarWorkout.SourceMenuName);
                Assert.Contains("胸", calendarWorkout.PrimaryMuscles);
                Assert.Equal(1760m, calendarWorkout.TotalVolume);
                Assert.Equal(3, calendarWorkout.TotalSets);

                await second.Analytics.SaveDashboardSettingsAsync(DashboardPeriod.ThreeMonths, [exerciseId]);
                var dashboard = await second.Analytics.GetDashboardAsync(DashboardPeriod.ThreeMonths, today, [exerciseId]);
                Assert.Equal(1, dashboard.Summary.WorkoutCount);
                Assert.Equal(1760m, dashboard.Summary.TotalVolume);
                Assert.Equal(3, dashboard.Summary.TotalSets);
                Assert.Equal(1, dashboard.Frequency.Sum(x => x.WorkoutCount));
                Assert.Equal(firstSessionId, Assert.Single(dashboard.RecentWorkouts).WorkoutSessionId);
                var muscleVolume = Assert.Single(dashboard.MuscleVolumes);
                Assert.Equal("胸", muscleVolume.Name);
                Assert.Equal(1760m, muscleVolume.Volume);
                var firstOneRepSeries = Assert.Single(dashboard.OneRepMaxSeries);
                Assert.Equal("kg", firstOneRepSeries.Unit);
                Assert.Equal(120.33333333333333333333333333m, Assert.Single(firstOneRepSeries.Points).Value);
                Assert.Equal(116.66666666666666666666666667m, WorkoutCalculations.EpleyOneRepMax(100m, 5));

                var firstStats = await second.Analytics.GetStatisticsAsync(exerciseId, DashboardPeriod.ThreeMonths, StatisticsMetric.EstimatedOneRepMax, today);
                Assert.Equal(120.33333333333333333333333333m, firstStats.CurrentOneRepMax);
                Assert.Equal(firstStats.CurrentOneRepMax, firstStats.BestOneRepMax);
                Assert.Equal(100m, firstStats.MaxWeight);
                Assert.Equal(1, firstStats.WorkoutCount);
                var renderPlan = ScottPlotChartAdapter.CreatePlan(firstStats.Series);
                Assert.False(renderPlan.IsEmpty);
                Assert.Equal("ベンチプレス", renderPlan.Title);
                Assert.Equal("kg", renderPlan.Unit);
                Assert.Single(renderPlan.X);
                Assert.Single(renderPlan.Y);

                var historyVm = new HistoryViewModel(second.History);
                await historyVm.InitializeAsync();
                var historyVmItem = Assert.Single(historyVm.Items);
                await historyVm.OpenDetailAsync(historyVmItem);
                Assert.True(historyVm.IsDrawerOpen);
                Assert.Equal(firstSessionId, historyVm.SelectedDetail?.WorkoutSessionId);

                var calendarVm = new CalendarViewModel(second.History);
                await calendarVm.InitializeAsync();
                var calendarCell = calendarVm.Cells.Single(x => x.Date == firstDate);
                calendarVm.SelectDay(calendarCell);
                Assert.Equal(firstSessionId, Assert.Single(calendarVm.SelectedWorkouts).WorkoutSessionId);
                await calendarVm.OpenDetailAsync(calendarVm.SelectedWorkouts[0]);
                Assert.True(calendarVm.IsDrawerOpen);

                var renamed = await second.MasterData.SaveExerciseAsync(new(exerciseId, "バーベルベンチプレス", chestId, (await second.MasterData.GetExercisesAsync()).Single(x => x.Id == exerciseId).SecondaryMuscleIds, "Round 3 renamed"));
                Assert.Equal("バーベルベンチプレス", renamed.Name);
                var snapshotAfterRename = await second.History.GetWorkoutDetailAsync(firstSessionId);
                Assert.NotNull(snapshotAfterRename);
                Assert.Equal("ベンチプレス", Assert.Single(snapshotAfterRename.Exercises).ExerciseNameSnapshot);

                clock.UtcNow = firstCompletedAt.AddDays(1).AddHours(-1);
                var secondStart = Assert.IsType<StartWorkoutResult.Started>(await second.Workout.StartAsync(menuId, today));
                var secondSessionId = secondStart.Workout.SessionId;
                var secondExercise = Assert.Single(secondStart.Workout.Exercises);
                Assert.Equal("バーベルベンチプレス", secondExercise.ExerciseName);
                var previous = secondExercise.PreviousPerformance;
                Assert.NotNull(previous);
                Assert.Equal(firstDate, previous.WorkoutDate);
                Assert.Equal([(1, 100m, 5), (2, 100m, 5), (3, 95m, 8)], previous.Sets.Select(x => (x.SetNumber, x.WeightKg, x.Reps)).ToArray());
                Assert.Equal([(100m, 5), (100m, 5), (95m, 8)], secondExercise.Sets.Select(x => (x.SuggestedWeightKg!.Value, x.SuggestedReps!.Value)).ToArray());
                Assert.All(secondExercise.Sets, x => { Assert.Null(x.WeightKg); Assert.Null(x.Reps); });

                var secondSet = second.Workout.Exercises.Single().Sets[0];
                Set(secondSet, 105m, 5, 8.5m);
                await second.AutoSave.FlushAsync();
                Assert.Equal(second.State.EditRevision, second.State.LastSavedRevision);
                Assert.Equal(WorkoutSaveState.Saved, second.State.SaveState);

                var firstCompletedSets = previous.Sets.Select(x => new CompletedSet(x.WeightKg, x.Reps)).ToArray();
                Assert.True(WorkoutCalculations.IsWeightPr(105m, firstCompletedSets));
                Assert.True(WorkoutCalculations.IsEstimatedOneRepMaxPr(new(105m, 5), firstCompletedSets));

                clock.UtcNow = firstCompletedAt.AddDays(1);
                await second.Workout.FinishAsync();
                Assert.Null(second.Workout.LastOperationError);
                Assert.False(second.State.IsActive);
                await AssertDraftCountAsync(second, 0);

                var orderedHistory = await second.History.GetHistoryPageAsync(HistoryQuery(25));
                Assert.Equal([secondSessionId, firstSessionId], orderedHistory.Items.Select(x => x.Id));
                Assert.Equal([525m, 1760m], orderedHistory.Items.Select(x => x.TotalVolume));

                var dashboardAfterPr = await second.Analytics.GetDashboardAsync(DashboardPeriod.ThreeMonths, today, [exerciseId]);
                Assert.Equal(2, dashboardAfterPr.Summary.WorkoutCount);
                Assert.Equal(2285m, dashboardAfterPr.Summary.TotalVolume);
                Assert.Equal(4, dashboardAfterPr.Summary.TotalSets);
                Assert.Equal(2, dashboardAfterPr.Frequency.Sum(x => x.WorkoutCount));
                Assert.Equal(2, dashboardAfterPr.RecentWorkouts.Count);
                Assert.Equal(2285m, Assert.Single(dashboardAfterPr.MuscleVolumes).Volume);
                var dashboardTrend = Assert.Single(dashboardAfterPr.OneRepMaxSeries);
                Assert.Equal([firstDate, today], dashboardTrend.Points.Select(x => x.Date));
                Assert.Equal([120.33333333333333333333333333m, 122.5m], dashboardTrend.Points.Select(x => x.Value));

                await AssertStatisticsAsync(second, exerciseId, today);
                var statsForPlan = await second.Analytics.GetStatisticsAsync(exerciseId, DashboardPeriod.ThreeMonths, StatisticsMetric.EstimatedOneRepMax, today);
                var plan = ScottPlotChartAdapter.CreatePlan(statsForPlan.Series);
                Assert.False(plan.IsEmpty);
                Assert.Equal(2, plan.X.Length);
                Assert.Equal([120.33333333333333d, 122.5d], plan.Y);
                Assert.All(statsForPlan.Series.Points, point => { Assert.NotNull(point.Label); Assert.NotNull(point.Tooltip); Assert.NotNull(point.SourceId); });
            }

            await using (var restarted = new Round3Host(databasePath, clock))
            {
                await restarted.InitializeAsync();
                await restarted.Workout.InitializeAsync();

                Assert.False(restarted.State.IsActive);
                Assert.Null(restarted.Workout.SessionId);
                await AssertDraftCountAsync(restarted, 0);
                Assert.Equal(ThemeMode.Dark, (await restarted.Settings.GetAsync()).ThemeMode);
                Assert.Equal("バーベルベンチプレス", (await restarted.MasterData.GetExercisesAsync()).Single(x => x.Id == exerciseId).Name);
                Assert.Equal("胸の日", (await restarted.MasterData.GetMenusAsync()).Single(x => x.Id == menuId).Name);

                var history = await restarted.History.GetHistoryPageAsync(HistoryQuery(25));
                Assert.Equal(2, history.Items.Count);
                var firstDetail = await restarted.History.GetWorkoutDetailAsync(firstSessionId);
                Assert.NotNull(firstDetail);
                Assert.Equal("ベンチプレス", Assert.Single(firstDetail.Exercises).ExerciseNameSnapshot);
                var calendar = await restarted.History.GetCalendarMonthAsync(today.Year, today.Month);
                Assert.Equal(2, calendar.Days.Sum(x => x.WorkoutCount));
                var dashboard = await restarted.Analytics.GetDashboardAsync(DashboardPeriod.ThreeMonths, today, [exerciseId]);
                Assert.Equal(2, dashboard.Summary.WorkoutCount);
                await AssertStatisticsAsync(restarted, exerciseId, today);

                await restarted.DashboardViewModel.InitializeAsync();
                await restarted.StatisticsViewModel.InitializeAsync();
                Assert.Equal("Loaded", restarted.DashboardViewModel.PageState);
                Assert.Equal(2, restarted.DashboardViewModel.Summary?.WorkoutCount);
                Assert.Equal("Loaded", restarted.StatisticsViewModel.PageState);
                Assert.Equal("122.5 kg", restarted.StatisticsViewModel.CurrentOneRepMaxText);
                Assert.False(ScottPlotChartAdapter.CreatePlan(restarted.StatisticsViewModel.Series).IsEmpty);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static HistoryQuery HistoryQuery(int pageSize) => new(null, null, null, null, null, pageSize, null);

    private static void Set(WorkoutSetViewModel set, decimal weight, int reps, decimal rpe)
    {
        set.WeightKg = weight;
        set.Reps = reps;
        set.Rpe = rpe;
    }

    private static (decimal Weight, int Reps, decimal Rpe)[] Values(ActiveWorkoutViewModel workout) =>
        workout.Exercises.Single().Sets.Select(x => (x.WeightKg!.Value, x.Reps!.Value, x.Rpe!.Value)).ToArray();

    private static async Task AssertSingleDraftAsync(Round3Host host, Guid sessionId, Guid menuId, string exerciseName, string muscleName)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var draft = await db.WorkoutSessions.AsNoTracking().Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleAsync(x => x.Status == WorkoutStatus.Draft);
        Assert.Equal(sessionId, draft.Id);
        Assert.Equal(menuId, draft.SourceMenuId);
        var exercise = Assert.Single(draft.Exercises);
        Assert.Equal(exerciseName, exercise.ExerciseNameSnapshot);
        Assert.Equal(muscleName, exercise.PrimaryMuscleNameSnapshot);
        Assert.Equal(3, exercise.Sets.Count);
    }

    private static async Task AssertDraftCountAsync(Round3Host host, int expected)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        Assert.Equal(expected, await db.WorkoutSessions.CountAsync(x => x.Status == WorkoutStatus.Draft));
    }

    private static async Task AssertSavedSetsAsync(Round3Host host, Guid sessionId, IReadOnlyList<(decimal Weight, int Reps, decimal Rpe)> expected)
    {
        await using var db = await host.Factory.CreateDbContextAsync();
        var rows = await db.WorkoutSets.AsNoTracking()
            .Where(x => x.WorkoutExercise.WorkoutSessionId == sessionId)
            .OrderBy(x => x.SetNumber)
            .Select(x => new { x.WeightKg, x.Reps, x.Rpe })
            .ToListAsync();
        Assert.Equal(expected, rows.Select(x => (x.WeightKg!.Value, x.Reps!.Value, x.Rpe!.Value)).ToArray());
    }

    private static async Task AssertStatisticsAsync(Round3Host host, Guid exerciseId, DateOnly today)
    {
        var oneRep = await host.Analytics.GetStatisticsAsync(exerciseId, DashboardPeriod.ThreeMonths, StatisticsMetric.EstimatedOneRepMax, today);
        Assert.Equal(122.5m, oneRep.CurrentOneRepMax);
        Assert.Equal(122.5m, oneRep.BestOneRepMax);
        Assert.Equal(105m, oneRep.MaxWeight);
        Assert.Equal(2, oneRep.WorkoutCount);
        Assert.Equal([120.33333333333333333333333333m, 122.5m], oneRep.Series.Points.Select(x => x.Value));

        var maxWeight = await host.Analytics.GetStatisticsAsync(exerciseId, DashboardPeriod.ThreeMonths, StatisticsMetric.MaxWeight, today);
        var volume = await host.Analytics.GetStatisticsAsync(exerciseId, DashboardPeriod.ThreeMonths, StatisticsMetric.Volume, today);
        var sets = await host.Analytics.GetStatisticsAsync(exerciseId, DashboardPeriod.ThreeMonths, StatisticsMetric.Sets, today);
        var reps = await host.Analytics.GetStatisticsAsync(exerciseId, DashboardPeriod.ThreeMonths, StatisticsMetric.Reps, today);
        Assert.Equal([100m, 105m], maxWeight.Series.Points.Select(x => x.Value));
        Assert.Equal([1760m, 525m], volume.Series.Points.Select(x => x.Value));
        Assert.Equal([3m, 1m], sets.Series.Points.Select(x => x.Value));
        Assert.Equal([8m, 5m], reps.Series.Points.Select(x => x.Value));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class Round3Host : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Round3Host(string databasePath, MutableClock clock)
        {
            Delay = new ControlledAutoSaveDelay();
            var services = new ServiceCollection();
            services.AddApplication();
            services.AddInfrastructure(databasePath);
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<IAutoSaveDelay>(Delay);
            _provider = services.BuildServiceProvider();

            Factory = Get<IDbContextFactory<WorkoutTrackerDbContext>>();
            MasterData = Get<MasterDataService>();
            History = Get<HistoryCalendarService>();
            Analytics = Get<AnalyticsService>();
            Settings = Get<SettingsService>();
            AutoSave = Get<AutoSaveCoordinator>();
            State = Get<ActiveWorkoutState>();
            Workout = new(
                Get<LoadActiveWorkoutUseCase>(), Get<StartWorkoutUseCase>(), Get<GetPreviousPerformanceUseCase>(),
                Get<FinishWorkoutUseCase>(), Get<DiscardWorkoutUseCase>(), AutoSave, State,
                Get<IExerciseRepository>(), Get<IWorkoutMenuRepository>(), Get<IOffUiExecutor>());
            DashboardViewModel = new(Analytics);
            StatisticsViewModel = new(Analytics);
        }

        public ControlledAutoSaveDelay Delay { get; }
        public IDbContextFactory<WorkoutTrackerDbContext> Factory { get; }
        public MasterDataService MasterData { get; }
        public HistoryCalendarService History { get; }
        public AnalyticsService Analytics { get; }
        public SettingsService Settings { get; }
        public AutoSaveCoordinator AutoSave { get; }
        public ActiveWorkoutState State { get; }
        public ActiveWorkoutViewModel Workout { get; }
        public DashboardViewModel DashboardViewModel { get; }
        public StatisticsViewModel StatisticsViewModel { get; }

        public Task InitializeAsync() => Get<IDatabaseInitializer>().InitializeFreshDatabaseAsync();
        private T Get<T>() where T : notnull => _provider.GetRequiredService<T>();
        public async ValueTask DisposeAsync() { await _provider.DisposeAsync(); SqliteConnection.ClearAllPools(); }
    }

    private sealed class MutableClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;
    }

    private sealed class ControlledAutoSaveDelay : IAutoSaveDelay
    {
        private readonly ConcurrentQueue<DelayRequest> _requests = new();
        public IReadOnlyList<TimeSpan> RequestedDelays => _requests.Select(x => x.Delay).ToArray();

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            var request = new DelayRequest(delay);
            request.Registration = cancellationToken.Register(() => request.Completion.TrySetCanceled(cancellationToken));
            _requests.Enqueue(request);
            return request.Completion.Task;
        }

        public void ReleaseLatest()
        {
            var request = _requests.LastOrDefault(x => !x.Completion.Task.IsCompleted)
                ?? throw new InvalidOperationException("No pending debounce request.");
            request.Completion.TrySetResult();
        }

        private sealed class DelayRequest(TimeSpan delay)
        {
            public TimeSpan Delay { get; } = delay;
            public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public CancellationTokenRegistration Registration { get; set; }
        }
    }
}
