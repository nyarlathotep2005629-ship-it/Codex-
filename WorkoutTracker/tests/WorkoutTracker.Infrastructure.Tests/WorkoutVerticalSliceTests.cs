using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class WorkoutVerticalSliceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 3, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "DB-010 fresh application startup applies InitialCreate migration")]
    public async Task Db010FreshStartup()
    {
        await using var f = new DatabaseTestFixture(); await f.Initializer.InitializeFreshDatabaseAsync();
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Single(await db.Database.GetAppliedMigrationsAsync()); Assert.True(await db.WorkoutSessions.CountAsync() == 0);
    }

    [Fact(DisplayName = "APP-008 and APP-009 Menu expands in order with DefaultSetCount")]
    public async Task App008App009()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        var seeded = await SeedMenuAsync(f, [3, 5, 2], [2, 0, 1]);
        var result = await f.VerticalSlice.StartAsync(new(seeded.MenuId, new DateOnly(2026, 8, 22)), Now);
        Assert.False(result.IsExistingDraft); var workout = result.Workout!;
        Assert.Equal(seeded.ExerciseIdsInExpectedOrder, workout.Exercises.Select(x => x.ExerciseId));
        Assert.Equal([5, 2, 3], workout.Exercises.Select(x => x.Sets.Count));
        Assert.All(workout.Exercises, x => Assert.Equal(Enumerable.Range(1, x.Sets.Count), x.Sets.Select(s => s.SetNumber)));
    }

    [Fact(DisplayName = "APP-010 Menu-less start creates one empty Draft")]
    public async Task App010()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var date = new DateOnly(2026, 8, 22);
        var result = await f.VerticalSlice.StartAsync(new(null, date), Now);
        Assert.False(result.IsExistingDraft); Assert.Null(result.Workout!.SourceMenuId); Assert.Empty(result.Workout.Exercises);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Single(await db.WorkoutSessions.Where(x => x.Status == WorkoutStatus.Draft).ToListAsync());
    }

    [Fact(DisplayName = "APP-011 existing Draft is returned and a second Draft is not created")]
    public async Task App011()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var first = await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, 22)), Now);
        var second = await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, 21)), Now.AddMinutes(1));
        Assert.True(second.IsExistingDraft); Assert.Equal(first.Workout!.SessionId, second.ExistingDraft!.SessionId);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.WorkoutSessions.CountAsync(x => x.Status == WorkoutStatus.Draft));
    }

    [Fact(DisplayName = "APP-006 concurrent Start requests converge to one Draft and ExistingDraft result")]
    public async Task App006Concurrent()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        using var barrier = new Barrier(2);
        async Task<StartPersistenceResult> StartAsync(int day)
        {
            barrier.SignalAndWait(); return await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, day)), Now);
        }
        var results = await Task.WhenAll(Task.Run(() => StartAsync(21)), Task.Run(() => StartAsync(22)));
        Assert.Single(results, x => !x.IsExistingDraft); Assert.Single(results, x => x.IsExistingDraft);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(1, await db.WorkoutSessions.CountAsync(x => x.Status == WorkoutStatus.Draft));
    }

    [Fact(DisplayName = "APP-012 Menu changes do not mutate expanded Draft snapshot")]
    public async Task App012()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var seeded = await SeedMenuAsync(f, [2], [0]);
        var started = await f.VerticalSlice.StartAsync(new(seeded.MenuId, new DateOnly(2026, 8, 22)), Now); var before = started.Workout!;
        await using (var db = await f.Factory.CreateDbContextAsync()) { var row = await db.MenuExercises.SingleAsync(); db.MenuExercises.Remove(row); await db.SaveChangesAsync(); }
        var after = await f.VerticalSlice.LoadDraftAsync();
        Assert.Equal(before.Exercises[0].ExerciseName, after!.Exercises[0].ExerciseName); Assert.Equal(2, after.Exercises[0].Sets.Count);
    }

    [Fact(DisplayName = "APP-014 deleting Source Menu sets Draft and Completed references null without deleting workout data")]
    public async Task App014()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var seeded = await SeedMenuAsync(f, [1], [0]);
        var draft = await f.VerticalSlice.StartAsync(new(seeded.MenuId, new DateOnly(2026, 8, 22)), Now);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            var menuExercise = await db.MenuExercises.Include(x => x.Exercise).ThenInclude(x => x.PrimaryMuscle).SingleAsync();
            var session = new WorkoutSession(Guid.NewGuid(), new DateOnly(2026, 8, 20), seeded.MenuId, "Completed", null, Now.AddDays(-2)); session.Complete(Now.AddDays(-2));
            var exercise = new WorkoutExercise(Guid.NewGuid(), session.Id, menuExercise.ExerciseId, menuExercise.Exercise.Name, menuExercise.Exercise.PrimaryMuscleId, menuExercise.Exercise.PrimaryMuscle.Name, 0, null, Now.AddDays(-2));
            db.AddRange(session, exercise, new WorkoutSet(Guid.NewGuid(), exercise.Id, 1, 50m, 10, null, Now.AddDays(-2))); await db.SaveChangesAsync();
            db.WorkoutMenus.Remove(await db.WorkoutMenus.SingleAsync()); await db.SaveChangesAsync();
        }
        await using var verify = await f.Factory.CreateDbContextAsync(); Assert.Equal(2, await verify.WorkoutSessions.CountAsync()); Assert.All(await verify.WorkoutSessions.ToListAsync(), x => Assert.Null(x.SourceMenuId));
        Assert.True(await verify.WorkoutExercises.CountAsync() >= 2); Assert.True(await verify.WorkoutSets.CountAsync() >= 2); Assert.Equal(draft.Workout!.SessionId, (await verify.WorkoutSessions.SingleAsync(x => x.Status == WorkoutStatus.Draft)).Id);
    }

    [Fact(DisplayName = "APP-016 and APP-017 Previous Performance selects latest Completed and ignores Draft")]
    public async Task App016App017()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedExerciseAsync(f, "Bench");
        await SeedWorkoutAsync(f, master, new DateOnly(2026, 8, 10), 90m, true, Now.AddDays(-10));
        await SeedWorkoutAsync(f, master, new DateOnly(2026, 8, 20), 100m, true, Now.AddDays(-2));
        await SeedWorkoutAsync(f, master, new DateOnly(2026, 8, 22), 200m, false, Now);
        var previous = await f.VerticalSlice.GetLatestCompletedPerformanceAsync(master.Exercise.Id);
        Assert.NotNull(previous); Assert.Equal(new DateOnly(2026, 8, 20), previous.WorkoutDate); Assert.Equal(100m, previous.Sets[0].WeightKg);
    }

    [Fact(DisplayName = "APP-018 and APP-022 Suggestions map only matching Previous SetNumber and never become actual values")]
    public async Task App018App022()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedExerciseAsync(f, "Press");
        var previousSession = new WorkoutSession(Guid.NewGuid(), new DateOnly(2026, 8, 20), null, null, null, Now.AddDays(-2)); previousSession.Complete(Now.AddDays(-2));
        var previousExercise = new WorkoutExercise(Guid.NewGuid(), previousSession.Id, master.Exercise.Id, master.Exercise.Name, master.Muscle.Id, master.Muscle.Name, 0, null, Now.AddDays(-2));
        await using (var db = await f.Factory.CreateDbContextAsync()) { db.AddRange(previousSession, previousExercise, new WorkoutSet(Guid.NewGuid(), previousExercise.Id, 1, 100m, 8, null, Now.AddDays(-2)), new WorkoutSet(Guid.NewGuid(), previousExercise.Id, 2, 90m, 10, null, Now.AddDays(-2))); await db.SaveChangesAsync(); }
        var menu = new WorkoutMenu(Guid.NewGuid(), "Press menu", null, Now); var row = new MenuExercise(Guid.NewGuid(), menu.Id, master.Exercise.Id, 0, 3, null, Now);
        await using (var db = await f.Factory.CreateDbContextAsync()) { db.AddRange(menu, row); await db.SaveChangesAsync(); }
        var state = new ActiveWorkoutState(); var useCase = new StartWorkoutUseCase(f.VerticalSlice, state, new FixedClock(), new InlineExecutor());
        var result = Assert.IsType<StartWorkoutResult.Started>(await useCase.ExecuteAsync(new(menu.Id, new DateOnly(2026, 8, 22)))); var sets = result.Workout.Exercises.Single().Sets;
        Assert.Equal(100m, sets[0].SuggestedWeightKg); Assert.Equal(90m, sets[1].SuggestedWeightKg); Assert.Null(sets[2].SuggestedWeightKg);
        Assert.All(sets, set => { Assert.Null(set.WeightKg); Assert.Null(set.Reps); });
    }

    [Fact(DisplayName = "APP-023 real Autosave commit can be rehydrated from SQLite")]
    public async Task App023RealPersistence()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedExerciseAsync(f, "Autosave");
        var started = await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, 22)), Now); var state = new ActiveWorkoutState(); state.SetStarted(started.Workout!);
        var setId = Guid.NewGuid(); var exerciseId = Guid.NewGuid();
        var dto = started.Workout! with { Exercises = [new(exerciseId, master.Exercise.Id, master.Exercise.Name, master.Muscle.Id, master.Muscle.Name, 0, null, null, [new(setId, 1, 80.25m, 10, 8.5m, null, null)])] };
        var request = new SaveDraftRequest(dto.SessionId, dto.WorkoutDate, dto.WorkoutName, null, [new(exerciseId, master.Exercise.Id, master.Exercise.Name, master.Muscle.Id, master.Muscle.Name, 0, null, [new(setId, 1, 80.25m, 10, 8.5m)])]);
        var coordinator = new AutoSaveCoordinator(f.VerticalSlice, state, new FixedClock(), new ImmediateDelay()); coordinator.NotifyChanged(dto, request, true); await coordinator.FlushAsync();
        var loaded = await f.VerticalSlice.LoadDraftAsync(); Assert.Equal(80.25m, loaded!.Exercises.Single().Sets.Single().WeightKg); Assert.Equal(WorkoutSaveState.Saved, state.SaveState);
    }

    [Fact(DisplayName = "APP-031 through APP-036 Finish removes empty Sets and completes with fixed UTC time")]
    public async Task App031To036()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedExerciseAsync(f, "Squat");
        var started = await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, 22)), Now); var sessionId = started.Workout!.SessionId;
        var exerciseId = Guid.NewGuid();
        await f.VerticalSlice.SaveDraftAsync(new(sessionId, new DateOnly(2026, 8, 22), null, null,
            [new(exerciseId, master.Exercise.Id, "ignored", master.Muscle.Id, "ignored", 0, null,
                [new(Guid.NewGuid(), 1, 100m, 5, 8m), new(Guid.NewGuid(), 2, null, null, null)])]), Now);
        var completedAt = Now.AddMinutes(30); var result = await f.VerticalSlice.FinishAsync(sessionId, completedAt);
        Assert.True(result.Succeeded);
        await using var db = await f.Factory.CreateDbContextAsync(); var completed = await db.WorkoutSessions.Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleAsync();
        Assert.Equal(WorkoutStatus.Completed, completed.Status); Assert.Equal(completedAt, completed.CompletedAtUtc); Assert.Single(completed.Exercises.Single().Sets);
        Assert.NotNull(await f.VerticalSlice.GetLatestCompletedPerformanceAsync(master.Exercise.Id));
    }

    [Theory(DisplayName = "APP-032 and APP-033 invalid Draft cannot Finish")]
    [InlineData(true, false)] [InlineData(false, true)] [InlineData(false, false)]
    public async Task App032App033(bool weight, bool reps)
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedExerciseAsync(f, "Deadlift");
        var started = await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, 22)), Now); var sessionId = started.Workout!.SessionId;
        await f.VerticalSlice.SaveDraftAsync(new(sessionId, new DateOnly(2026, 8, 22), null, null,
            [new(Guid.NewGuid(), master.Exercise.Id, master.Exercise.Name, master.Muscle.Id, master.Muscle.Name, 0, null,
                [new(Guid.NewGuid(), 1, weight ? 100m : null, reps ? 5 : null, null)])]), Now);
        var result = await f.VerticalSlice.FinishAsync(sessionId, Now.AddMinutes(1)); Assert.False(result.Succeeded);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(WorkoutStatus.Draft, (await db.WorkoutSessions.SingleAsync()).Status);
    }

    [Fact(DisplayName = "APP-038 Discard CASCADE deletes Draft graph")]
    public async Task App038()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var seeded = await SeedMenuAsync(f, [2], [0]);
        var started = await f.VerticalSlice.StartAsync(new(seeded.MenuId, new DateOnly(2026, 8, 22)), Now); await f.VerticalSlice.DiscardAsync(started.Workout!.SessionId);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(0, await db.WorkoutSessions.CountAsync()); Assert.Equal(0, await db.WorkoutExercises.CountAsync()); Assert.Equal(0, await db.WorkoutSets.CountAsync());
    }

    [Fact(DisplayName = "APP-039 APP-040 and ORD-005 save add/remove with contiguous unique ordering")]
    public async Task App039App040Ord005()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var first = await SeedExerciseAsync(f, "Row"); var second = await SeedExerciseAsync(f, "Curl");
        var started = await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, 22)), Now); var sessionId = started.Workout!.SessionId;
        var firstExerciseId = Guid.NewGuid(); var firstSet = Guid.NewGuid(); var secondSet = Guid.NewGuid();
        await f.VerticalSlice.SaveDraftAsync(new(sessionId, new DateOnly(2026, 8, 22), null, null,
            [new(firstExerciseId, first.Exercise.Id, first.Exercise.Name, first.Muscle.Id, first.Muscle.Name, 0, null, [new(firstSet, 1, 50m, 8, null), new(secondSet, 2, null, null, null)]),
             new(Guid.NewGuid(), second.Exercise.Id, second.Exercise.Name, second.Muscle.Id, second.Muscle.Name, 1, null, [new(Guid.NewGuid(), 1, null, null, null)])]), Now);
        await f.VerticalSlice.SaveDraftAsync(new(sessionId, new DateOnly(2026, 8, 22), null, null,
            [new(firstExerciseId, first.Exercise.Id, first.Exercise.Name, first.Muscle.Id, first.Muscle.Name, 0, null, [new(secondSet, 1, null, null, null)])]), Now.AddMinutes(1));
        var loaded = await f.VerticalSlice.LoadDraftAsync(); Assert.Single(loaded!.Exercises); Assert.Single(loaded.Exercises[0].Sets); Assert.Equal(1, loaded.Exercises[0].Sets[0].SetNumber);
    }

    [Fact(DisplayName = "VAL-011 Active Workout rejects a 51st Exercise")]
    public async Task Val011()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var started = await f.VerticalSlice.StartAsync(new(null, new DateOnly(2026, 8, 22)), Now);
        var rows = Enumerable.Range(0, 51).Select(index => new SaveDraftExerciseRequest(Guid.NewGuid(), Guid.NewGuid(), $"Exercise {index}", Guid.NewGuid(), "Muscle", index, null, [new(Guid.NewGuid(), 1, null, null, null)])).ToArray();
        await Assert.ThrowsAsync<DomainValidationException>(() => f.VerticalSlice.SaveDraftAsync(new(started.Workout!.SessionId, new DateOnly(2026, 8, 22), null, null, rows), Now));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(0, await db.WorkoutExercises.CountAsync());
    }

    [Fact(DisplayName = "ACT-003 and ACT-008 Rehydrate restores committed Draft data")]
    public async Task Act003Act008()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var seeded = await SeedMenuAsync(f, [1], [0]);
        var started = await f.VerticalSlice.StartAsync(new(seeded.MenuId, new DateOnly(2026, 8, 22)), Now); var loaded = await f.VerticalSlice.LoadDraftAsync();
        Assert.Equal(started.Workout!.SessionId, loaded!.SessionId); Assert.Single(loaded.Exercises); Assert.Single(loaded.Exercises[0].Sets);
        var state = new ActiveWorkoutState(); state.SetRehydrated(loaded); Assert.Equal(0, state.EditRevision); Assert.Equal(0, state.LastSavedRevision); Assert.Equal(WorkoutSaveState.Saved, state.SaveState); Assert.False(state.HasUnsavedChanges);
    }

    private static async Task<(Guid MenuId, Guid[] ExerciseIdsInExpectedOrder)> SeedMenuAsync(DatabaseTestFixture f, int[] setCounts, int[] storedOrders)
    {
        var now = Now; var muscle = new MuscleGroup(Guid.NewGuid(), $"Muscle-{Guid.NewGuid():N}", 0, now); var menu = new WorkoutMenu(Guid.NewGuid(), "Menu", null, now);
        var exercises = setCounts.Select((_, i) => new Exercise(Guid.NewGuid(), $"Exercise-{i}-{Guid.NewGuid():N}", muscle.Id, null, now)).ToArray();
        var rows = exercises.Select((exercise, i) => new MenuExercise(Guid.NewGuid(), menu.Id, exercise.Id, storedOrders[i], setCounts[i], $"Note-{i}", now)).ToArray();
        await using var db = await f.Factory.CreateDbContextAsync(); db.Add(muscle); db.Add(menu); db.AddRange(exercises); db.AddRange(rows); await db.SaveChangesAsync();
        return (menu.Id, rows.OrderBy(x => x.DisplayOrder).Select(x => x.ExerciseId).ToArray());
    }

    private static async Task<(MuscleGroup Muscle, Exercise Exercise)> SeedExerciseAsync(DatabaseTestFixture f, string name)
    {
        var muscle = new MuscleGroup(Guid.NewGuid(), $"Muscle-{Guid.NewGuid():N}", 0, Now); var exercise = new Exercise(Guid.NewGuid(), $"{name}-{Guid.NewGuid():N}", muscle.Id, null, Now);
        await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(muscle, exercise); await db.SaveChangesAsync(); return (muscle, exercise);
    }

    private static async Task SeedWorkoutAsync(DatabaseTestFixture f, (MuscleGroup Muscle, Exercise Exercise) master, DateOnly date, decimal weight, bool completed, DateTimeOffset now)
    {
        var session = new WorkoutSession(Guid.NewGuid(), date, null, null, null, now); if (completed) session.Complete(now);
        var workoutExercise = new WorkoutExercise(Guid.NewGuid(), session.Id, master.Exercise.Id, master.Exercise.Name, master.Muscle.Id, master.Muscle.Name, 0, null, now);
        var set = new WorkoutSet(Guid.NewGuid(), workoutExercise.Id, 1, weight, 5, null, now);
        await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(session, workoutExercise, set); await db.SaveChangesAsync();
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => Now; }
    private sealed class InlineExecutor : IOffUiExecutor
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class ImmediateDelay : IAutoSaveDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }
}
