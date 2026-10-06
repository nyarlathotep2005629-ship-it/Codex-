using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class MasterDataTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 6, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "MST-001 Exercise create persists primary secondary note active and UTC timestamps")]
    public async Task Mst001()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var (primary, secondary) = await AddMusclesAsync(f);
        var saved = await f.MasterData.SaveExerciseAsync(new(null, "  Bench Press  ", primary.Id, [secondary.Id], " note "), Now);
        Assert.NotEqual(Guid.Empty, saved.Id); Assert.Equal("Bench Press", saved.Name); Assert.True(saved.IsActive); Assert.Equal([secondary.Id], saved.SecondaryMuscleIds); Assert.Equal(Now, saved.UpdatedAtUtc);
        await using var db = await f.Factory.CreateDbContextAsync(); var entity = await db.Exercises.SingleAsync(); Assert.Equal(Now, entity.CreatedAtUtc); Assert.Equal(Now, entity.UpdatedAtUtc); Assert.Single(await db.ExerciseSecondaryMuscles.ToListAsync());
    }

    [Fact(DisplayName = "MST-002 Exercise edit changes current master while Completed Snapshot remains exact")]
    public async Task Mst002()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var (oldMuscle, newMuscle) = await AddMusclesAsync(f);
        var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Old Bench", oldMuscle.Id, [], "old"), Now); var snapshot = await AddCompletedAsync(f, exercise.Id, "Old Bench", oldMuscle.Id, oldMuscle.Name);
        var updated = await f.MasterData.SaveExerciseAsync(new(exercise.Id, "New Bench", newMuscle.Id, [oldMuscle.Id], "new"), Now.AddMinutes(1));
        Assert.Equal("New Bench", updated.Name); Assert.Equal(newMuscle.Id, updated.PrimaryMuscleId); Assert.True(updated.UpdatedAtUtc > exercise.UpdatedAtUtc);
        await using var db = await f.Factory.CreateDbContextAsync(); var past = await db.WorkoutExercises.SingleAsync(x => x.Id == snapshot.WorkoutExerciseId);
        Assert.Equal("Old Bench", past.ExerciseNameSnapshot); Assert.Equal(oldMuscle.Id, past.PrimaryMuscleIdSnapshot); Assert.Equal(oldMuscle.Name, past.PrimaryMuscleNameSnapshot);
    }

    [Fact(DisplayName = "MST-003 Exercise hide removes active choice while preserving Completed display and volume")]
    public async Task Mst003()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Chest", 0); var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Bench", muscle.Id, [], null), Now); var snapshot = await AddCompletedAsync(f, exercise.Id, "Bench", muscle.Id, "Chest");
        await f.MasterData.SetExerciseActiveAsync(exercise.Id, false, Now.AddMinutes(1));
        Assert.Empty(await f.MasterData.GetExercisesAsync(false));
        await using var db = await f.Factory.CreateDbContextAsync(); var past = await db.WorkoutExercises.Include(x => x.Sets).SingleAsync(x => x.Id == snapshot.WorkoutExerciseId);
        Assert.Equal("Bench", past.ExerciseNameSnapshot); Assert.Equal(500m, WorkoutCalculations.TotalVolume(past.Sets.Select(x => new RecordedSet(x.WeightKg, x.Reps)))); Assert.False((await db.Exercises.SingleAsync()).IsActive);
    }

    [Fact(DisplayName = "MST-004 unused Exercise physical delete cascades secondary muscles only")]
    public async Task Mst004()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var (primary, secondary) = await AddMusclesAsync(f); var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Unused", primary.Id, [secondary.Id], null), Now);
        Assert.Equal(ExerciseDeleteResult.Deleted, await f.MasterData.DeleteExerciseAsync(exercise.Id));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Empty(await db.Exercises.ToListAsync()); Assert.Empty(await db.ExerciseSecondaryMuscles.ToListAsync()); Assert.Equal(2, await db.MuscleGroups.CountAsync());
    }

    [Fact(DisplayName = "MST-005 referenced Exercise physical delete is rejected without changing master or references")]
    public async Task Mst005()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Back", 0); var menuExercise = await f.MasterData.SaveExerciseAsync(new(null, "Row", muscle.Id, [], null), Now); var workoutExercise = await f.MasterData.SaveExerciseAsync(new(null, "Pulldown", muscle.Id, [], null), Now);
        var menu = await f.MasterData.SaveMenuAsync(new(null, "Pull", null, [new(null, menuExercise.Id, 3, null)]), Now); await AddCompletedAsync(f, workoutExercise.Id, "Pulldown", muscle.Id, "Back");
        Assert.Equal(ExerciseDeleteResult.ReferencedMustHide, await f.MasterData.DeleteExerciseAsync(menuExercise.Id)); Assert.Equal(ExerciseDeleteResult.ReferencedMustHide, await f.MasterData.DeleteExerciseAsync(workoutExercise.Id));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.All(await db.Exercises.ToListAsync(), x => Assert.True(x.IsActive)); Assert.Single(await db.MenuExercises.ToListAsync()); Assert.Equal(menu.Id, (await db.MenuExercises.SingleAsync()).WorkoutMenuId); Assert.Single(await db.WorkoutExercises.ToListAsync());
    }

    [Fact(DisplayName = "MST-006 Menu create stores children order planned sets and notes atomically")]
    public async Task Mst006()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Legs", 0); var a = await f.MasterData.SaveExerciseAsync(new(null, "Squat", muscle.Id, [], null), Now); var b = await f.MasterData.SaveExerciseAsync(new(null, "Curl", muscle.Id, [], null), Now);
        var menu = await f.MasterData.SaveMenuAsync(new(null, "Leg Day", "description", [new(null, b.Id, 5, "first"), new(null, a.Id, 3, "second")]), Now);
        Assert.Equal([b.Id, a.Id], menu.Exercises.Select(x => x.ExerciseId)); Assert.Equal([0, 1], menu.Exercises.Select(x => x.DisplayOrder)); Assert.Equal([5, 3], menu.Exercises.Select(x => x.DefaultSetCount)); Assert.Equal(8, menu.PlannedSetCount);
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveMenuAsync(new(null, "Broken", null, [new(null, Guid.NewGuid(), 3, null)]), Now));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Single(await db.WorkoutMenus.ToListAsync());
    }

    [Fact(DisplayName = "MST-007 Menu rename keeps child IDs and workout Snapshot unchanged")]
    public async Task Mst007()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Chest", 0); var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Bench", muscle.Id, [], null), Now); var menu = await f.MasterData.SaveMenuAsync(new(null, "Old", null, [new(null, exercise.Id, 3, "note")]), Now);
        var started = await f.VerticalSlice.StartAsync(new(menu.Id, new DateOnly(2026, 8, 22)), Now.AddMinutes(1)); var childId = menu.Exercises.Single().Id;
        var renamed = await f.MasterData.SaveMenuAsync(new(menu.Id, "New", "desc", menu.Exercises.Select(x => new SaveMenuExerciseRequest(x.Id, x.ExerciseId, x.DefaultSetCount, x.Note)).ToList()), Now.AddMinutes(2));
        Assert.Equal(menu.Id, renamed.Id); Assert.Equal(childId, renamed.Exercises.Single().Id); Assert.True(renamed.UpdatedAtUtc > menu.UpdatedAtUtc); Assert.Equal("Bench", (await f.VerticalSlice.LoadDraftAsync())!.Exercises.Single().ExerciseName); Assert.Equal(started.Workout!.SessionId, (await f.VerticalSlice.LoadDraftAsync())!.SessionId);
    }

    [Fact(DisplayName = "MST-008 Menu edit changes template only and never mutates active Draft Snapshot")]
    public async Task Mst008()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Arms", 0); var a = await f.MasterData.SaveExerciseAsync(new(null, "Curl", muscle.Id, [], null), Now); var b = await f.MasterData.SaveExerciseAsync(new(null, "Extension", muscle.Id, [], null), Now); var menu = await f.MasterData.SaveMenuAsync(new(null, "Arms", null, [new(null, a.Id, 2, "old")]), Now);
        var started = await f.VerticalSlice.StartAsync(new(menu.Id, new DateOnly(2026, 8, 22)), Now.AddMinutes(1)); var completed = await AddCompletedAsync(f, a.Id, "Curl", muscle.Id, "Arms", menu.Id);
        var edited = await f.MasterData.SaveMenuAsync(new(menu.Id, "Arms", null, [new(null, b.Id, 4, "new")]), Now.AddMinutes(2)); var draft = await f.VerticalSlice.LoadDraftAsync();
        Assert.Equal([b.Id], edited.Exercises.Select(x => x.ExerciseId)); Assert.Equal(4, edited.Exercises.Single().DefaultSetCount); Assert.Equal([a.Id], draft!.Exercises.Select(x => x.ExerciseId)); Assert.Equal(2, draft.Exercises.Single().Sets.Count); Assert.Equal(started.Workout!.SessionId, draft.SessionId);
        await using var db = await f.Factory.CreateDbContextAsync(); var past = await db.WorkoutExercises.Include(x => x.Sets).SingleAsync(x => x.Id == completed.WorkoutExerciseId); Assert.Equal("Curl", past.ExerciseNameSnapshot); Assert.Equal(500m, WorkoutCalculations.TotalVolume(past.Sets.Select(x => new RecordedSet(x.WeightKg, x.Reps))));
    }

    [Fact(DisplayName = "MST-009 and ORD-001 MenuExercise reorder uses two-stage offset in one rollback-safe transaction")]
    public async Task Mst009Ord001()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "All", 0); var a = await f.MasterData.SaveExerciseAsync(new(null, "A", muscle.Id, [], null), Now); var b = await f.MasterData.SaveExerciseAsync(new(null, "B", muscle.Id, [], null), Now); var c = await f.MasterData.SaveExerciseAsync(new(null, "C", muscle.Id, [], null), Now); var menu = await f.MasterData.SaveMenuAsync(new(null, "ABC", null, [new(null, a.Id, 1, null), new(null, b.Id, 2, null), new(null, c.Id, 3, null)]), Now);
        await using (var db = await f.Factory.CreateDbContextAsync()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER AbortFinalOrder BEFORE UPDATE OF DisplayOrder ON MenuExercises WHEN OLD.DisplayOrder >= 1000000 AND NEW.DisplayOrder < 1000000 BEGIN SELECT RAISE(ABORT, 'final order failure'); END;");
        var reordered = new SaveWorkoutMenuRequest(menu.Id, menu.Name, menu.Description, [new(menu.Exercises[2].Id, c.Id, 3, null), new(menu.Exercises[0].Id, a.Id, 1, null), new(menu.Exercises[1].Id, b.Id, 2, null)]);
        await Assert.ThrowsAnyAsync<Exception>(() => f.MasterData.SaveMenuAsync(reordered, Now.AddMinutes(1)));
        await using (var verify = await f.Factory.CreateDbContextAsync()) { Assert.Equal([a.Id, b.Id, c.Id], await verify.MenuExercises.OrderBy(x => x.DisplayOrder).Select(x => x.ExerciseId).ToListAsync()); Assert.Equal([0, 1, 2], await verify.MenuExercises.OrderBy(x => x.DisplayOrder).Select(x => x.DisplayOrder).ToListAsync()); await verify.Database.ExecuteSqlRawAsync("DROP TRIGGER AbortFinalOrder;"); }
        var saved = await f.MasterData.SaveMenuAsync(reordered, Now.AddMinutes(2)); Assert.Equal([c.Id, a.Id, b.Id], saved.Exercises.Select(x => x.ExerciseId)); Assert.Equal([0, 1, 2], saved.Exercises.Select(x => x.DisplayOrder));
    }

    [Fact(DisplayName = "MST-010 APP-013 and APP-014 Menu delete cascades definition and nulls source while retaining Draft and Completed graphs")]
    public async Task Mst010App013App014()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Chest", 0); var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Bench", muscle.Id, [], null), Now); var menu = await f.MasterData.SaveMenuAsync(new(null, "Push", null, [new(null, exercise.Id, 1, null)]), Now); var draft = await f.VerticalSlice.StartAsync(new(menu.Id, new DateOnly(2026, 8, 22)), Now.AddMinutes(1)); await AddCompletedAsync(f, exercise.Id, "Bench", muscle.Id, "Chest", menu.Id);
        await f.MasterData.DeleteMenuAsync(menu.Id);
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Empty(await db.WorkoutMenus.ToListAsync()); Assert.Empty(await db.MenuExercises.ToListAsync()); Assert.Equal(2, await db.WorkoutSessions.CountAsync()); Assert.All(await db.WorkoutSessions.ToListAsync(), x => Assert.Null(x.SourceMenuId)); Assert.Equal(2, await db.WorkoutExercises.CountAsync()); Assert.Equal(2, await db.WorkoutSets.CountAsync()); Assert.Equal(draft.Workout!.SessionId, (await db.WorkoutSessions.SingleAsync(x => x.Status == WorkoutStatus.Draft)).Id);
    }

    [Fact(DisplayName = "APP-015 Menu Duplicate copies content in one transaction and remains independently editable")]
    public async Task App015()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Back", 0); var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Row", muscle.Id, [], null), Now); var source = await f.MasterData.SaveMenuAsync(new(null, "Pull", "desc", [new(null, exercise.Id, 4, "note")]), Now);
        var copy = await f.MasterData.DuplicateMenuAsync(source.Id, "Pull Copy", Now.AddMinutes(1)); Assert.NotEqual(source.Id, copy.Id); Assert.NotEqual(source.Exercises.Single().Id, copy.Exercises.Single().Id); Assert.Equal(source.Description, copy.Description); Assert.Equal(source.Exercises.Single().DefaultSetCount, copy.Exercises.Single().DefaultSetCount);
        await f.MasterData.SaveMenuAsync(new(copy.Id, "Edited Copy", null, [new(copy.Exercises.Single().Id, exercise.Id, 2, "changed")]), Now.AddMinutes(2)); var menus = await f.MasterData.GetMenusAsync(); Assert.Equal(4, menus.Single(x => x.Id == source.Id).Exercises.Single().DefaultSetCount); Assert.Equal("Pull", menus.Single(x => x.Id == source.Id).Name);
    }

    [Fact(DisplayName = "DB-018 Snapshot is not an FK and survives old MuscleGroup deletion after current master reassignment")]
    public async Task Db018Snapshot()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var (oldMuscle, newMuscle) = await AddMusclesAsync(f); var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Press", oldMuscle.Id, [], null), Now); var snapshot = await AddCompletedAsync(f, exercise.Id, "Press", oldMuscle.Id, oldMuscle.Name);
        Assert.Equal(MuscleGroupDeleteResult.Referenced, await f.MasterData.DeleteMuscleGroupAsync(oldMuscle.Id));
        await f.MasterData.SaveExerciseAsync(new(exercise.Id, "Press New", newMuscle.Id, [], null), Now.AddMinutes(1)); Assert.Equal(MuscleGroupDeleteResult.Deleted, await f.MasterData.DeleteMuscleGroupAsync(oldMuscle.Id));
        await using var db = await f.Factory.CreateDbContextAsync(); var past = await db.WorkoutExercises.Include(x => x.Sets).SingleAsync(x => x.Id == snapshot.WorkoutExerciseId); Assert.Equal(oldMuscle.Id, past.PrimaryMuscleIdSnapshot); Assert.Equal(oldMuscle.Name, past.PrimaryMuscleNameSnapshot); Assert.Equal("Press", past.ExerciseNameSnapshot); Assert.Equal(500m, WorkoutCalculations.TotalVolume(past.Sets.Select(x => new RecordedSet(x.WeightKg, x.Reps))));
    }

    [Fact(DisplayName = "VAL-009 VAL-012 VAL-013 VAL-014 and VAL-015 Master Data boundaries reject invalid input without truncation")]
    public async Task MasterValidation()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = await AddMuscleAsync(f, "Core", 0);
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveExerciseAsync(new(null, " ", muscle.Id, [], null), Now));
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveExerciseAsync(new(null, new string('x', 101), muscle.Id, [], null), Now));
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveExerciseAsync(new(null, "Valid", muscle.Id, [], new string('x', 2001)), Now));
        var exercise = await f.MasterData.SaveExerciseAsync(new(null, "Valid", muscle.Id, [], new string('x', 2000)), Now);
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveMenuAsync(new(null, new string('x', 101), null, []), Now));
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveMenuAsync(new(null, "Menu", null, [new(null, exercise.Id, 0, null)]), Now));
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveMenuAsync(new(null, "Menu", null, [new(null, exercise.Id, 21, null)]), Now));
        await Assert.ThrowsAsync<DomainValidationException>(() => f.MasterData.SaveMenuAsync(new(null, "Menu", null, [new(null, exercise.Id, 1, new string('x', 1001))]), Now));
        var valid = await f.MasterData.SaveMenuAsync(new(null, new string('m', 100), new string('d', 2000), [new(null, exercise.Id, 20, new string('n', 1000))]), Now); Assert.Equal(20, valid.Exercises.Single().DefaultSetCount);
    }

    private static async Task<(MuscleGroupDto First, MuscleGroupDto Second)> AddMusclesAsync(DatabaseTestFixture f) => (await AddMuscleAsync(f, "Primary", 0), await AddMuscleAsync(f, "Secondary", 1));
    private static Task<MuscleGroupDto> AddMuscleAsync(DatabaseTestFixture f, string name, int order) => f.MasterData.SaveMuscleGroupAsync(new(null, name, order), Now);
    private static async Task<(Guid WorkoutExerciseId, Guid SessionId)> AddCompletedAsync(DatabaseTestFixture f, Guid exerciseId, string exerciseName, Guid muscleId, string muscleName, Guid? sourceMenuId = null)
    {
        var session = new WorkoutSession(Guid.NewGuid(), new DateOnly(2026, 8, 20), sourceMenuId, "Past", null, Now.AddDays(-2)); session.Complete(Now.AddDays(-2));
        var workoutExercise = new WorkoutExercise(Guid.NewGuid(), session.Id, exerciseId, exerciseName, muscleId, muscleName, 0, null, Now.AddDays(-2)); var set = new WorkoutSet(Guid.NewGuid(), workoutExercise.Id, 1, 50m, 10, null, Now.AddDays(-2));
        await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(session, workoutExercise, set); await db.SaveChangesAsync(); return (workoutExercise.Id, session.Id);
    }
}
