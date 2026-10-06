using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

/// <summary>
/// Android M1 boundary regressions use a fresh app-private-style SQLite file. Shared business
/// rules remain covered by their own suites; these tests assert the product vertical-flow seams.
/// </summary>
public sealed class AndroidM1ProductFlowTests
{
    [Fact(DisplayName = "ANDROID-M1-001 new local DB starts both Menu and Empty Workout")]
    public async Task AndroidM1001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var menuId = await SeedMenuAsync(fixture);

        var started = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(menuId, Today())));
        Assert.Single(started.Workout.Exercises);
        await fixture.VerticalSlice.DiscardAsync(started.Workout.SessionId);
        fixture.ActiveWorkout.Clear();

        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today())));
        Assert.Empty(empty.Workout.Exercises);
    }

    [Fact(DisplayName = "ANDROID-M1-002 set edit autosaves into the local SQLite Draft")]
    public async Task AndroidM1002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var draft = await StartMenuAsync(fixture);
        var edited = WithCompletedSet(draft, 82.5m, 6, 8m);

        fixture.AutoSave.BeginWorkout();
        fixture.AutoSave.NotifyChanged(edited, SaveRequest(edited), immediate: false);
        await fixture.AutoSave.FlushAsync();

        var committed = await fixture.VerticalSlice.LoadDraftAsync();
        Assert.Equal(82.5m, committed!.Exercises.Single().Sets.Single().WeightKg);
        Assert.Equal(WorkoutSaveState.Saved, fixture.ActiveWorkout.SaveState);
    }

    [Fact(DisplayName = "ANDROID-M1-003 rehydrate reconstructs the committed local Draft")]
    public async Task AndroidM1003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var draft = WithCompletedSet(await StartMenuAsync(fixture), 90m, 5, 8.5m);
        fixture.AutoSave.BeginWorkout();
        fixture.AutoSave.NotifyChanged(draft, SaveRequest(draft), immediate: true);
        await fixture.AutoSave.FlushAsync();

        fixture.ActiveWorkout.Clear();
        var restored = await new LoadActiveWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, new TaskSchedulerOffUiExecutor()).ExecuteAsync();

        Assert.NotNull(restored);
        Assert.Equal(draft.SessionId, restored.SessionId);
        Assert.Equal(90m, restored.Exercises.Single().Sets.Single().WeightKg);
        Assert.Equal(8.5m, restored.Exercises.Single().Sets.Single().Rpe);
        Assert.Equal(0, fixture.ActiveWorkout.EditRevision);
    }

    [Fact(DisplayName = "ANDROID-M1-004 finishing a Draft produces a Completed Workout")]
    public async Task AndroidM1004()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var draft = WithCompletedSet(await StartMenuAsync(fixture), 100m, 5, 8m);
        fixture.AutoSave.BeginWorkout();
        fixture.AutoSave.NotifyChanged(draft, SaveRequest(draft), immediate: true);
        await fixture.AutoSave.FlushAsync();

        var result = await new FinishWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, fixture.AutoSave, new FixedClock(), new TaskSchedulerOffUiExecutor()).ExecuteAsync();
        Assert.True(result.Succeeded);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(WorkoutStatus.Completed, (await db.WorkoutSessions.SingleAsync()).Status);
    }

    [Fact(DisplayName = "ANDROID-M1-005 completed Workout appears in the History query")]
    public async Task AndroidM1005()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var draft = WithCompletedSet(await StartMenuAsync(fixture), 70m, 10, null);
        await fixture.VerticalSlice.SaveDraftAsync(SaveRequest(draft), DateTimeOffset.UtcNow);
        Assert.True((await fixture.VerticalSlice.FinishAsync(draft.SessionId, DateTimeOffset.UtcNow)).Succeeded);

        var history = await fixture.HistoryCalendar.GetHistoryPageAsync(new HistoryQuery(null, null, null, null, null, 25, null));
        Assert.Single(history.Items);
        Assert.Equal(draft.SessionId, history.Items[0].Id);
    }

    [Fact(DisplayName = "ANDROID-M1-006 completed snapshot remains after Menu master data changes")]
    public async Task AndroidM1006()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var draft = WithCompletedSet(await StartMenuAsync(fixture), 60m, 12, null);
        var snapshotName = draft.Exercises.Single().ExerciseName;
        await fixture.VerticalSlice.SaveDraftAsync(SaveRequest(draft), DateTimeOffset.UtcNow);
        Assert.True((await fixture.VerticalSlice.FinishAsync(draft.SessionId, DateTimeOffset.UtcNow)).Succeeded);

        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.WorkoutMenus.Remove(await db.WorkoutMenus.SingleAsync());
            await db.SaveChangesAsync();
        }
        var detail = await fixture.HistoryCalendar.GetWorkoutDetailAsync(draft.SessionId);
        Assert.Equal(snapshotName, detail!.Exercises.Single().ExerciseNameSnapshot);
    }

    [Fact(DisplayName = "ANDROID-M1-007 second local Draft is prohibited")]
    public async Task AndroidM1007()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var first = await StartMenuAsync(fixture);
        var second = await fixture.VerticalSlice.StartAsync(new StartWorkoutRequest(null, Today()), DateTimeOffset.UtcNow);

        Assert.True(second.IsExistingDraft);
        Assert.Equal(first.SessionId, second.ExistingDraft!.SessionId);
    }

    [Fact(DisplayName = "ANDROID-M1-008 failed save retains the in-memory edit for retry")]
    public async Task AndroidM1008()
    {
        var initial = new ActiveWorkoutDto(Guid.NewGuid(), null, "Local", Today(), null, []);
        var edited = initial with { WorkoutName = "入力を保持" };
        var state = new ActiveWorkoutState();
        state.SetStarted(initial);
        var autoSave = new AutoSaveCoordinator(new SaveFailingPersistence(), state, new FixedClock(), new ImmediateDelay());
        autoSave.BeginWorkout();
        autoSave.NotifyChanged(edited, SaveRequest(edited), immediate: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => autoSave.FlushAsync());
        Assert.Equal(WorkoutSaveState.Failed, state.SaveState);
        Assert.Equal("入力を保持", state.CurrentWorkout!.WorkoutName);
    }

    [Fact(DisplayName = "ANDROID-M1-HIST-001 completed workout is available to the full History list")]
    public async Task AndroidM1Hist001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteMenuWorkoutAsync(fixture, 75m, 8, Today());

        var page = await fixture.HistoryCalendar.GetHistoryPageAsync(new(null, null, null, null, null, 25, null));
        var item = Assert.Single(page.Items);
        Assert.Equal(completed.SessionId, item.Id);
        Assert.Equal(1, item.ExerciseCount);
        Assert.Equal(1, item.TotalSets);
        Assert.Equal(600m, item.TotalVolume);
    }

    [Fact(DisplayName = "ANDROID-M1-HIST-002 detail uses the completed snapshot and includes recorded fields")]
    public async Task AndroidM1Hist002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteMenuWorkoutAsync(fixture, 82.5m, 6, Today(), 8.5m);

        var detail = await fixture.HistoryCalendar.GetWorkoutDetailAsync(completed.SessionId);
        var set = Assert.Single(Assert.Single(detail!.Exercises).Sets);
        Assert.Equal(completed.Exercises.Single().ExerciseName, detail.Exercises.Single().ExerciseNameSnapshot);
        Assert.Equal(82.5m, set.WeightKg);
        Assert.Equal(6, set.Reps);
        Assert.Equal(8.5m, set.Rpe);
        Assert.Equal(495m, detail.TotalVolume);
    }

    [Fact(DisplayName = "ANDROID-M1-HIST-003 History ordering is correct")]
    public async Task AndroidM1Hist003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var first = await CompleteMenuWorkoutAsync(fixture, 50m, 10, Today().AddDays(-1));
        var second = await CompleteMenuWorkoutAsync(fixture, 60m, 10, Today());

        var page = await fixture.HistoryCalendar.GetHistoryPageAsync(new(null, null, null, null, null, 25, null));
        Assert.Equal([second.SessionId, first.SessionId], page.Items.Select(x => x.Id));
    }

    [Fact(DisplayName = "ANDROID-M1-HIST-004 Filter applies existing query semantics")]
    public async Task AndroidM1Hist004()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var first = await CompleteMenuWorkoutAsync(fixture, 50m, 10, Today());
        var second = await CompleteMenuWorkoutAsync(fixture, 60m, 10, Today());
        var options = await fixture.HistoryCalendar.GetHistoryFilterOptionsAsync();

        var page = await fixture.HistoryCalendar.GetHistoryPageAsync(new(Today(), Today().AddDays(1), null, null, first.MenuId, 25, null));
        Assert.Contains(options.Menus, x => x.Id == first.MenuId);
        Assert.Single(page.Items);
        Assert.Equal(first.SessionId, page.Items[0].Id);
        Assert.NotEqual(first.SessionId, second.SessionId);
    }

    [Fact(DisplayName = "ANDROID-M1-HIST-005 Pagination does not duplicate Workout rows")]
    public async Task AndroidM1Hist005()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        for (var index = 0; index < 26; index++) await CompleteMenuWorkoutAsync(fixture, 40m + index, 5, Today());

        var first = await fixture.HistoryCalendar.GetHistoryPageAsync(new(null, null, null, null, null, 25, null));
        var second = await fixture.HistoryCalendar.GetHistoryPageAsync(new(null, null, null, null, null, 25, first.NextCursor));
        Assert.Equal(25, first.Items.Count);
        Assert.Single(second.Items);
        Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
    }

    [Fact(DisplayName = "ANDROID-M1-CAL-001 a completed workout creates a marker on its calendar date")]
    public async Task AndroidM1Cal001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteMenuWorkoutAsync(fixture, 80m, 5, Today());
        var month = await fixture.HistoryCalendar.GetCalendarMonthAsync(Today().Year, Today().Month);
        var day = month.Days.Single(x => x.Date == completed.WorkoutDate);
        Assert.True(day.HasWorkouts);
        Assert.Equal(completed.SessionId, Assert.Single(day.Workouts).WorkoutSessionId);
    }

    [Fact(DisplayName = "ANDROID-M1-CAL-002 calendar provides an ordinary empty day")]
    public async Task AndroidM1Cal002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var month = await fixture.HistoryCalendar.GetCalendarMonthAsync(Today().Year, Today().Month);
        Assert.All(month.Days, day => Assert.False(day.HasWorkouts));
    }

    [Fact(DisplayName = "ANDROID-M1-CAL-003 selected-day data opens the same completed session identity")]
    public async Task AndroidM1Cal003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteMenuWorkoutAsync(fixture, 90m, 4, Today());
        var month = await fixture.HistoryCalendar.GetCalendarMonthAsync(Today().Year, Today().Month);
        var selected = month.Days.Single(x => x.Date == Today());
        var detail = await fixture.HistoryCalendar.GetWorkoutDetailAsync(Assert.Single(selected.Workouts).WorkoutSessionId);
        Assert.Equal(completed.SessionId, detail!.WorkoutSessionId);
    }

    [Fact(DisplayName = "ANDROID-M1-CAL-004 previous and next month queries retain Japanese calendar month boundaries")]
    public async Task AndroidM1Cal004()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var thisMonth = await fixture.HistoryCalendar.GetCalendarMonthAsync(Today().Year, Today().Month);
        var next = Today().AddMonths(1);
        var nextMonth = await fixture.HistoryCalendar.GetCalendarMonthAsync(next.Year, next.Month);
        Assert.All(thisMonth.Days, x => Assert.Equal(Today().Month, x.Date.Month));
        Assert.All(nextMonth.Days, x => Assert.Equal(next.Month, x.Date.Month));
    }

    [Fact(DisplayName = "ANDROID-M1-CAL-005 local calendar remains readable after active-workout state is cleared")]
    public async Task AndroidM1Cal005()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteMenuWorkoutAsync(fixture, 70m, 7, Today());
        fixture.ActiveWorkout.Clear();

        var month = await fixture.HistoryCalendar.GetCalendarMonthAsync(Today().Year, Today().Month);
        Assert.Contains(month.Days.Single(x => x.Date == completed.WorkoutDate).Workouts, x => x.WorkoutSessionId == completed.SessionId);
    }

    [Fact(DisplayName = "ANDROID-M1-MASTER-001 Fresh DB allows first Muscle Group creation")]
    public async Task AndroidM1Master001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var muscle = await fixture.MasterData.SaveMuscleGroupAsync(new(null, "胸", 0), MasterNow());
        Assert.Equal("胸", Assert.Single(await fixture.MasterData.GetMuscleGroupsAsync()).Name);
        Assert.Equal(0, muscle.DisplayOrder);
    }

    [Fact(DisplayName = "ANDROID-M1-MASTER-002 Exercise can be created using Muscle Group")]
    public async Task AndroidM1Master002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var muscle = await fixture.MasterData.SaveMuscleGroupAsync(new(null, "背中", 0), MasterNow());
        var exercise = await fixture.MasterData.SaveExerciseAsync(new(null, "ロー", muscle.Id, [], "フォーム確認"), MasterNow());
        Assert.Equal(muscle.Id, exercise.PrimaryMuscleId);
        Assert.True(exercise.IsActive);
    }

    [Fact(DisplayName = "ANDROID-M1-MASTER-003 Exercise edit persists")]
    public async Task AndroidM1Master003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var muscle = await fixture.MasterData.SaveMuscleGroupAsync(new(null, "脚", 0), MasterNow());
        var exercise = await fixture.MasterData.SaveExerciseAsync(new(null, "スクワット", muscle.Id, [], null), MasterNow());
        await fixture.MasterData.SaveExerciseAsync(new(exercise.Id, "ハイバースクワット", muscle.Id, [], "編集済み", false), MasterNow());
        var saved = Assert.Single(await fixture.MasterData.GetExercisesAsync(true));
        Assert.Equal("ハイバースクワット", saved.Name);
        Assert.False(saved.IsActive);
    }

    [Fact(DisplayName = "ANDROID-M1-MASTER-004 Referenced Exercise follows existing delete and deactivate rule")]
    public async Task AndroidM1Master004()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        Assert.Equal(ExerciseDeleteResult.ReferencedMustHide, await fixture.MasterData.DeleteExerciseAsync(setup.Exercise.Id));
        await fixture.MasterData.SetExerciseActiveAsync(setup.Exercise.Id, false, MasterNow());
        Assert.False((await fixture.MasterData.GetExercisesAsync(true)).Single().IsActive);
    }

    [Fact(DisplayName = "ANDROID-M1-MENU-001 Menu can be created")]
    public async Task AndroidM1Menu001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var muscle = await fixture.MasterData.SaveMuscleGroupAsync(new(null, "肩", 0), MasterNow());
        var exercise = await fixture.MasterData.SaveExerciseAsync(new(null, "ショルダープレス", muscle.Id, [], null), MasterNow());
        var menu = await fixture.MasterData.SaveMenuAsync(new(null, "肩の日", "基本", [new(null, exercise.Id, 3, null)]), MasterNow());
        Assert.Equal("肩の日", menu.Name);
    }

    [Fact(DisplayName = "ANDROID-M1-MENU-002 Exercise can be added to Menu")]
    public async Task AndroidM1Menu002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        Assert.Equal(setup.Exercise.Id, Assert.Single(setup.Menu.Exercises).ExerciseId);
    }

    [Fact(DisplayName = "ANDROID-M1-MENU-003 Duplicate Exercise is rejected")]
    public async Task AndroidM1Menu003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        await Assert.ThrowsAsync<DomainValidationException>(() => fixture.MasterData.SaveMenuAsync(new(null, "重複", null, [new(null, setup.Exercise.Id, 3, null), new(null, setup.Exercise.Id, 3, null)]), MasterNow()));
    }

    [Fact(DisplayName = "ANDROID-M1-MENU-004 Menu order persists")]
    public async Task AndroidM1Menu004()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var second = await fixture.MasterData.SaveExerciseAsync(new(null, "フライ", setup.Muscle.Id, [], null), MasterNow());
        var edited = await fixture.MasterData.SaveMenuAsync(new(setup.Menu.Id, setup.Menu.Name, null, [new(null, second.Id, 2, null), new(setup.Menu.Exercises.Single().Id, setup.Exercise.Id, 3, null)]), MasterNow());
        Assert.Equal([second.Id, setup.Exercise.Id], edited.Exercises.Select(x => x.ExerciseId));
        Assert.Equal([0, 1], edited.Exercises.Select(x => x.DisplayOrder));
    }

    [Fact(DisplayName = "ANDROID-M1-MENU-005 PlannedSetCount persists")]
    public async Task AndroidM1Menu005()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture, 5);
        Assert.Equal(5, setup.Menu.PlannedSetCount);
        Assert.Equal(5, (await fixture.MasterData.GetMenusAsync()).Single().PlannedSetCount);
    }

    [Fact(DisplayName = "ANDROID-M1-FRESH-001 Fresh DB Master Data Menu starts a Workout")]
    public async Task AndroidM1Fresh001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var started = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(setup.Menu.Id, Today())));
        Assert.Equal(setup.Exercise.Id, Assert.Single(started.Workout.Exercises).ExerciseId);
    }

    [Fact(DisplayName = "ANDROID-M1-FRESH-002 Finished fresh-install Workout appears in History")]
    public async Task AndroidM1Fresh002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteFreshWorkoutAsync(fixture);
        var item = Assert.Single((await fixture.HistoryCalendar.GetHistoryPageAsync(new(null, null, null, null, null, 25, null))).Items);
        Assert.Equal(completed.SessionId, item.Id);
    }

    [Fact(DisplayName = "ANDROID-M1-FRESH-003 Finished fresh-install Workout appears in Calendar")]
    public async Task AndroidM1Fresh003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteFreshWorkoutAsync(fixture);
        var day = (await fixture.HistoryCalendar.GetCalendarMonthAsync(Today().Year, Today().Month)).Days.Single(x => x.Date == Today());
        Assert.Equal(completed.SessionId, Assert.Single(day.Workouts).WorkoutSessionId);
    }

    [Fact(DisplayName = "ANDROID-M1-FRESH-004 completed snapshot survives Exercise rename and Muscle reassignment")]
    public async Task AndroidM1Fresh004()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var completed = await CompleteFreshWorkoutAsync(fixture, setup);
        var newMuscle = await fixture.MasterData.SaveMuscleGroupAsync(new(null, "上半身", 1), MasterNow());
        await fixture.MasterData.SaveExerciseAsync(new(setup.Exercise.Id, "新しい種目名", newMuscle.Id, [], null), MasterNow());
        var detail = await fixture.HistoryCalendar.GetWorkoutDetailAsync(completed.SessionId);
        Assert.Equal(setup.Exercise.Name, detail!.Exercises.Single().ExerciseNameSnapshot);
        Assert.Equal(setup.Muscle.Name, detail.Exercises.Single().PrimaryMuscleNameSnapshot);
    }

    [Fact(DisplayName = "ANDROID-M1-STAT-001 Statistics returns an ordinary empty state before a completed workout")]
    public async Task AndroidM1Stat001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);

        var statistics = await fixture.Analytics.GetStatisticsAsync(new(ThreeMonthPeriod(), setup.Exercise.Id, StatisticsMetric.EstimatedOneRepMax));

        Assert.Empty(statistics.Series.Points);
        Assert.Equal(0, statistics.WorkoutCount);
    }

    [Fact(DisplayName = "ANDROID-M1-STAT-002 Estimated 1RM uses completed local workout data")]
    public async Task AndroidM1Stat002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        await CompleteFreshWorkoutAsync(fixture, setup);

        var statistics = await fixture.Analytics.GetStatisticsAsync(new(ThreeMonthPeriod(), setup.Exercise.Id, StatisticsMetric.EstimatedOneRepMax));

        Assert.Equal(280m / 3m, Assert.Single(statistics.Series.Points).Value);
        Assert.Equal(280m / 3m, statistics.CurrentOneRepMax);
    }

    [Theory(DisplayName = "ANDROID-M1-STAT-003 and ANDROID-M1-STAT-004 Volume, Sets, and Reps use shared metric queries")]
    [InlineData(StatisticsMetric.Volume, 400d)]
    [InlineData(StatisticsMetric.Sets, 1d)]
    [InlineData(StatisticsMetric.Reps, 5d)]
    public async Task AndroidM1Stat003And004(StatisticsMetric metric, double expected)
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        await CompleteFreshWorkoutAsync(fixture, setup);

        var statistics = await fixture.Analytics.GetStatisticsAsync(new(ThreeMonthPeriod(), setup.Exercise.Id, metric));

        Assert.Equal((decimal)expected, Assert.Single(statistics.Series.Points).Value);
    }

    [Fact(DisplayName = "ANDROID-M1-DASH-001 Dashboard recent workout and KPI come from completed local data")]
    public async Task AndroidM1Dash001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var completed = await CompleteFreshWorkoutAsync(fixture);

        var dashboard = await fixture.Analytics.GetDashboardAsync(new(ThreeMonthPeriod(), DashboardPeriod.ThreeMonths, []));

        Assert.Equal(1, dashboard.Summary.WorkoutCount);
        Assert.Equal(completed.SessionId, Assert.Single(dashboard.RecentWorkouts).WorkoutSessionId);
        Assert.Equal(400m, dashboard.Summary.TotalVolume);
    }

    [Fact(DisplayName = "ANDROID-M1-DASH-002 Dashboard excludes an active Draft from completed KPI")]
    public async Task AndroidM1Dash002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        await CompleteFreshWorkoutAsync(fixture);
        var draft = await StartMenuAsync(fixture);

        var dashboard = await fixture.Analytics.GetDashboardAsync(new(ThreeMonthPeriod(), DashboardPeriod.ThreeMonths, []));

        Assert.Equal(1, dashboard.Summary.WorkoutCount);
        Assert.DoesNotContain(dashboard.RecentWorkouts, x => x.WorkoutSessionId == draft.SessionId);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-001 empty Draft supports the menu, direct-add, and discard product paths")]
    public async Task AndroidWorkoutUx001()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;

        var direct = ActiveWorkoutDraftComposer.AddMissing(empty, [new(setup.Exercise, 1, null, null)]);
        Assert.Single(direct.Exercises);
        await DiscardAsync(fixture);
        Assert.Null(await fixture.VerticalSlice.LoadDraftAsync());
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-002 Exercise can be added to an empty Active Draft")]
    public async Task AndroidWorkoutUx002()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;

        var updated = ActiveWorkoutDraftComposer.AddMissing(empty, [new(setup.Exercise, 1, null, null)]);

        var exercise = Assert.Single(updated.Exercises);
        Assert.Equal(setup.Exercise.Id, exercise.ExerciseId);
        Assert.Single(exercise.Sets);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-003 Exercise can be added after the Workout has started")]
    public async Task AndroidWorkoutUx003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var extra = await CreateFreshMenuAsync(fixture);
        var started = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(setup.Menu.Id, Today()))).Workout;

        var updated = ActiveWorkoutDraftComposer.AddMissing(started, [new(extra.Exercise, 1, null, null)]);

        Assert.Equal(2, updated.Exercises.Count);
        Assert.Equal(extra.Exercise.Id, updated.Exercises.Last().ExerciseId);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-004 selecting a Menu on an empty Workout preserves planned set counts")]
    public async Task AndroidWorkoutUx004()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateTwoExerciseMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;

        var updated = AddMenuExercises(empty, setup.Menu, [setup.Exercise, setup.ExtraExercise]);

        Assert.Equal(2, updated.Exercises.Count);
        Assert.Equal(setup.Menu.Exercises[0].DefaultSetCount, updated.Exercises[0].Sets.Count);
        Assert.Equal(setup.Menu.Exercises[1].DefaultSetCount, updated.Exercises[1].Sets.Count);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-005 menu merge preserves entered sets and adds only missing Exercises")]
    public async Task AndroidWorkoutUx005()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateTwoExerciseMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;
        var entered = ActiveWorkoutDraftComposer.AddMissing(empty, [new(setup.Exercise, 1, null, null)]);
        entered = WithCompletedSet(entered, 80m, 5, 8m);

        var merged = AddMenuExercises(entered, setup.Menu, [setup.Exercise, setup.ExtraExercise]);

        Assert.Equal(2, merged.Exercises.Count);
        Assert.Equal(80m, merged.Exercises.Single(x => x.ExerciseId == setup.Exercise.Id).Sets.Single().WeightKg);
        Assert.Single(merged.Exercises, x => x.ExerciseId == setup.Exercise.Id);
        Assert.Equal(setup.ExtraExercise.Id, merged.Exercises.Last().ExerciseId);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-006 directly adding the same Exercise twice does not duplicate it")]
    public async Task AndroidWorkoutUx006()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;
        var once = ActiveWorkoutDraftComposer.AddMissing(empty, [new(setup.Exercise, 1, null, null)]);

        var twice = ActiveWorkoutDraftComposer.AddMissing(once, [new(setup.Exercise, 1, null, null)]);

        Assert.Single(twice.Exercises);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-007 directly added Exercise survives autosave and rehydrate")]
    public async Task AndroidWorkoutUx007()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;
        var updated = ActiveWorkoutDraftComposer.AddMissing(empty, [new(setup.Exercise, 1, null, null)]);

        fixture.AutoSave.BeginWorkout();
        fixture.AutoSave.NotifyChanged(updated, SaveRequest(updated), immediate: true);
        await fixture.AutoSave.FlushAsync();
        fixture.ActiveWorkout.Clear();
        var restored = await new LoadActiveWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, new TaskSchedulerOffUiExecutor()).ExecuteAsync();

        Assert.Equal(setup.Exercise.Id, Assert.Single(restored!.Exercises).ExerciseId);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-008 menu-added Exercises survive autosave and rehydrate")]
    public async Task AndroidWorkoutUx008()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateTwoExerciseMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;
        var updated = AddMenuExercises(empty, setup.Menu, [setup.Exercise, setup.ExtraExercise]);

        fixture.AutoSave.BeginWorkout();
        fixture.AutoSave.NotifyChanged(updated, SaveRequest(updated), immediate: true);
        await fixture.AutoSave.FlushAsync();
        fixture.ActiveWorkout.Clear();
        var restored = await new LoadActiveWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, new TaskSchedulerOffUiExecutor()).ExecuteAsync();

        Assert.Equal(2, restored!.Exercises.Count);
        Assert.Equal(3, restored.Exercises.Single(x => x.ExerciseId == setup.Exercise.Id).Sets.Count);
        Assert.Equal(2, restored.Exercises.Single(x => x.ExerciseId == setup.ExtraExercise.Id).Sets.Count);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-009 canceling an empty Workout clears the Draft without History")]
    public async Task AndroidWorkoutUx009()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        _ = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today())));

        await DiscardAsync(fixture);

        Assert.Null(await fixture.VerticalSlice.LoadDraftAsync());
        Assert.Empty((await fixture.HistoryCalendar.GetHistoryPageAsync(new(null, null, null, null, null, 25, null))).Items);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-010 canceling a partial Workout preserves master data and creates no Completed Workout")]
    public async Task AndroidWorkoutUx010()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var beforeExercises = (await fixture.MasterData.GetExercisesAsync(includeInactive: true)).Count;
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;
        var partial = WithCompletedSet(ActiveWorkoutDraftComposer.AddMissing(empty, [new(setup.Exercise, 1, null, null)]), 70m, 8, null);
        await fixture.VerticalSlice.SaveDraftAsync(SaveRequest(partial), DateTimeOffset.UtcNow);

        await DiscardAsync(fixture);

        Assert.Equal(beforeExercises, (await fixture.MasterData.GetExercisesAsync(includeInactive: true)).Count);
        Assert.Empty((await fixture.HistoryCalendar.GetHistoryPageAsync(new(null, null, null, null, null, 25, null))).Items);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-011 canceled Draft does not rehydrate after a cold start")]
    public async Task AndroidWorkoutUx011()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        _ = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today())));
        await DiscardAsync(fixture);

        var restored = await new LoadActiveWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, new TaskSchedulerOffUiExecutor()).ExecuteAsync();

        Assert.Null(restored);
        Assert.False(fixture.ActiveWorkout.IsActive);
    }

    [Fact(DisplayName = "ANDROID-WORKOUT-UX-012 completion validation still rejects a Draft with no valid Set")]
    public async Task AndroidWorkoutUx012()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var setup = await CreateFreshMenuAsync(fixture);
        var empty = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, Today()))).Workout;
        var updated = ActiveWorkoutDraftComposer.AddMissing(empty, [new(setup.Exercise, 1, null, null)]);
        fixture.AutoSave.BeginWorkout();
        fixture.AutoSave.NotifyChanged(updated, SaveRequest(updated), immediate: true);
        await fixture.AutoSave.FlushAsync();

        var result = await new FinishWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, fixture.AutoSave, new FixedClock(), new TaskSchedulerOffUiExecutor()).ExecuteAsync();

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, x => x.Contains("WeightとReps", StringComparison.Ordinal));
    }

    private static async Task<ActiveWorkoutDto> StartMenuAsync(DatabaseTestFixture fixture)
    {
        var menuId = await SeedMenuAsync(fixture);
        return Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(menuId, Today()))).Workout;
    }

    private static async Task<Guid> SeedMenuAsync(DatabaseTestFixture fixture)
    {
        var now = DateTimeOffset.UtcNow;
        var muscle = new MuscleGroup(Guid.NewGuid(), $"Android Muscle {Guid.NewGuid():N}", 0, now);
        var exercise = new Exercise(Guid.NewGuid(), $"Android Exercise {Guid.NewGuid():N}", muscle.Id, null, now);
        var menu = new WorkoutMenu(Guid.NewGuid(), $"Android Menu {Guid.NewGuid():N}", null, now);
        var item = new MenuExercise(Guid.NewGuid(), menu.Id, exercise.Id, 0, 1, null, now);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        db.AddRange(muscle, exercise, menu, item);
        await db.SaveChangesAsync();
        return menu.Id;
    }

    private static async Task<(MuscleGroupDto Muscle, ExerciseMasterDto Exercise, WorkoutMenuMasterDto Menu)> CreateFreshMenuAsync(DatabaseTestFixture fixture, int plannedSetCount = 3)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var muscle = await fixture.MasterData.SaveMuscleGroupAsync(new(null, $"胸-{suffix}", 0), MasterNow());
        var exercise = await fixture.MasterData.SaveExerciseAsync(new(null, $"ベンチプレス-{suffix}", muscle.Id, [], "Fresh install"), MasterNow());
        var menu = await fixture.MasterData.SaveMenuAsync(new(null, $"プッシュ-{suffix}", "Fresh install", [new(null, exercise.Id, plannedSetCount, "メイン")]), MasterNow());
        return (muscle, exercise, menu);
    }

    private static async Task<(ExerciseMasterDto Exercise, ExerciseMasterDto ExtraExercise, WorkoutMenuMasterDto Menu)> CreateTwoExerciseMenuAsync(DatabaseTestFixture fixture)
    {
        var suffix = Guid.NewGuid().ToString("N")[..6];
        var muscle = await fixture.MasterData.SaveMuscleGroupAsync(new(null, $"背-{suffix}", 0), MasterNow());
        var first = await fixture.MasterData.SaveExerciseAsync(new(null, $"ロー-{suffix}", muscle.Id, [], null), MasterNow());
        var second = await fixture.MasterData.SaveExerciseAsync(new(null, $"プル-{suffix}", muscle.Id, [], null), MasterNow());
        var menu = await fixture.MasterData.SaveMenuAsync(new(null, $"背中-{suffix}", null, [new(null, first.Id, 3, "主運動"), new(null, second.Id, 2, "補助運動")]), MasterNow());
        return (first, second, menu);
    }

    private static ActiveWorkoutDto AddMenuExercises(ActiveWorkoutDto draft, WorkoutMenuMasterDto menu, IReadOnlyList<ExerciseMasterDto> exercises)
    {
        var byId = exercises.ToDictionary(x => x.Id);
        return ActiveWorkoutDraftComposer.AddMissing(draft, menu.Exercises.OrderBy(x => x.DisplayOrder).Select(item =>
            new ActiveWorkoutExerciseAddition(byId[item.ExerciseId], item.DefaultSetCount, item.Note, null)));
    }

    private static async Task DiscardAsync(DatabaseTestFixture fixture)
    {
        var discarded = await new DiscardWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, fixture.AutoSave, new TaskSchedulerOffUiExecutor()).ExecuteAsync(confirmed: true);
        Assert.True(discarded);
    }

    private static async Task<(Guid SessionId, DateOnly WorkoutDate)> CompleteFreshWorkoutAsync(DatabaseTestFixture fixture, (MuscleGroupDto Muscle, ExerciseMasterDto Exercise, WorkoutMenuMasterDto Menu)? setup = null)
    {
        var source = setup ?? await CreateFreshMenuAsync(fixture);
        var draft = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(source.Menu.Id, Today()))).Workout;
        var completed = WithCompletedSet(draft, 80m, 5, 8m);
        await fixture.VerticalSlice.SaveDraftAsync(SaveRequest(completed), DateTimeOffset.UtcNow);
        Assert.True((await fixture.VerticalSlice.FinishAsync(completed.SessionId, DateTimeOffset.UtcNow)).Succeeded);
        fixture.ActiveWorkout.Clear();
        return (completed.SessionId, completed.WorkoutDate);
    }

    private static ActiveWorkoutDto WithCompletedSet(ActiveWorkoutDto draft, decimal weight, int reps, decimal? rpe)
    {
        var exercise = draft.Exercises.Single();
        var set = exercise.Sets.OrderBy(x => x.SetNumber).First() with { WeightKg = weight, Reps = reps, Rpe = rpe };
        return draft with { Exercises = [exercise with { Sets = [set] }] };
    }

    private static async Task<(Guid SessionId, Guid MenuId, DateOnly WorkoutDate, IReadOnlyList<ActiveWorkoutExerciseDto> Exercises)> CompleteMenuWorkoutAsync(
        DatabaseTestFixture fixture, decimal weight, int reps, DateOnly date, decimal? rpe = null)
    {
        var menuId = await SeedMenuAsync(fixture);
        var draft = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(menuId, date))).Workout;
        var completed = WithCompletedSet(draft, weight, reps, rpe);
        await fixture.VerticalSlice.SaveDraftAsync(SaveRequest(completed), DateTimeOffset.UtcNow);
        Assert.True((await fixture.VerticalSlice.FinishAsync(completed.SessionId, DateTimeOffset.UtcNow)).Succeeded);
        fixture.ActiveWorkout.Clear();
        return (completed.SessionId, menuId, completed.WorkoutDate, completed.Exercises);
    }

    private static SaveDraftRequest SaveRequest(ActiveWorkoutDto workout) => new(
        workout.SessionId, workout.WorkoutDate, workout.WorkoutName, workout.WorkoutNote,
        workout.Exercises.Select(exercise => new SaveDraftExerciseRequest(
            exercise.WorkoutExerciseId, exercise.ExerciseId, exercise.ExerciseName, exercise.PrimaryMuscleId, exercise.PrimaryMuscleName,
            exercise.DisplayOrder, exercise.Note, exercise.Sets.Select(set => new SaveDraftSetRequest(set.WorkoutSetId, set.SetNumber, set.WeightKg, set.Reps, set.Rpe)).ToArray())).ToArray());

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);
    private static AnalyticsPeriod ThreeMonthPeriod() => AnalyticsPeriodCalculator.Resolve(DashboardPeriod.ThreeMonths, Today());
    private static DateTimeOffset MasterNow() => DateTimeOffset.UtcNow;
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
    private sealed class ImmediateDelay : IAutoSaveDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }

    private sealed class SaveFailingPersistence : IWorkoutVerticalSlicePersistence
    {
        public Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Injected save failure.");
        public Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
