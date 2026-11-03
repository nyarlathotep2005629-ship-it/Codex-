using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class CompletedWorkoutDateTests
{
    private static readonly Guid SessionId = Guid.Parse("00000000-0000-0000-0000-000000000321");
    private static readonly DateTimeOffset Created = new(2026, 8, 10, 8, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Completed = Created.AddHours(1);

    [Fact(DisplayName = "DATE-001 completed date edit preserves all recorded values and timestamps except UpdatedAt")]
    public async Task ChangeDatePreservesWorkoutDataAndTimestamps()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var ids = await SeedCompletedAsync(f);
        var original = await ReadSessionAsync(f);
        var changedDate = new DateOnly(2026, 7, 4);

        Assert.True(await f.ChangeCompletedWorkoutDate.ExecuteAsync(SessionId, changedDate));

        var changed = await ReadSessionAsync(f);
        Assert.Equal(changedDate, changed.WorkoutDate);
        Assert.Equal(original.CreatedAtUtc, changed.CreatedAtUtc);
        Assert.Equal(original.CompletedAtUtc, changed.CompletedAtUtc);
        Assert.True(changed.UpdatedAtUtc > original.UpdatedAtUtc);
        Assert.Equal(original.Name, changed.Name); Assert.Equal(original.Note, changed.Note); Assert.Equal(original.SourceMenuId, changed.SourceMenuId);
        Assert.Equal(original.ExerciseNameSnapshot, changed.ExerciseNameSnapshot); Assert.Equal(original.MuscleNameSnapshot, changed.MuscleNameSnapshot);
        Assert.Equal(original.ExerciseNote, changed.ExerciseNote); Assert.Equal(original.WeightKg, changed.WeightKg);
        Assert.Equal(original.Reps, changed.Reps); Assert.Equal(original.Rpe, changed.Rpe); Assert.Equal(ids, (changed.ExerciseId, changed.SetId));

        await using var reopened = await f.Factory.CreateDbContextAsync();
        Assert.Equal(changedDate, (await reopened.WorkoutSessions.SingleAsync(x => x.Id == SessionId)).WorkoutDate);
    }

    [Fact(DisplayName = "DATE-002 absent and Draft sessions are not editable")]
    public async Task MissingAndDraftAreNotEdited()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var muscle = new MuscleGroup(Guid.NewGuid(), "Chest", 0, Created); var exercise = new Exercise(Guid.NewGuid(), "Press", muscle.Id, null, Created);
        await using (var db = await f.Factory.CreateDbContextAsync()) { db.AddRange(muscle, exercise, new WorkoutSession(SessionId, new(2026, 8, 10), null, null, null, Created)); await db.SaveChangesAsync(); }
        Assert.False(await f.ChangeCompletedWorkoutDate.ExecuteAsync(SessionId, new(2026, 8, 9)));
        Assert.False(await f.ChangeCompletedWorkoutDate.ExecuteAsync(Guid.NewGuid(), new(2026, 8, 9)));
        await using var verify = await f.Factory.CreateDbContextAsync(); Assert.Equal(new DateOnly(2026, 8, 10), (await verify.WorkoutSessions.SingleAsync()).WorkoutDate);
    }

    [Fact(DisplayName = "DATE-003 dates before 1900 and after local today are rejected")]
    public async Task InvalidDatesAreRejected()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.ChangeCompletedWorkoutDate.ExecuteAsync(SessionId, new(1899, 12, 31)));
        var tomorrow = DateOnly.FromDateTime(DateTime.Now).AddDays(1);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.ChangeCompletedWorkoutDate.ExecuteAsync(SessionId, tomorrow));
    }

    [Fact(DisplayName = "DATE-004 SQL failure rolls back date and UpdatedAt atomically")]
    public async Task SaveFailureLeavesRecordUnchanged()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); await SeedCompletedAsync(f);
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectWorkoutDateUpdate BEFORE UPDATE OF WorkoutDate ON WorkoutSessions BEGIN SELECT RAISE(ABORT, 'injected date update failure'); END;");
        }
        await Assert.ThrowsAnyAsync<Exception>(() => f.ChangeCompletedWorkoutDate.ExecuteAsync(SessionId, new(2026, 7, 4)));
        var unchanged = await ReadSessionAsync(f); Assert.Equal(new DateOnly(2026, 8, 10), unchanged.WorkoutDate); Assert.Equal(Completed, unchanged.UpdatedAtUtc);
    }

    [Fact(DisplayName = "DATE-005 changed date moves history calendar and statistics to the new day")]
    public async Task DateEditMovesHistoryCalendarAndStatistics()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var ids = await SeedCompletedAsync(f);
        Assert.True(await f.ChangeCompletedWorkoutDate.ExecuteAsync(SessionId, new(2026, 7, 4)));

        var july = await f.HistoryCalendar.GetCalendarMonthAsync(2026, 7);
        Assert.Contains(july.Days.Single(x => x.Date == new DateOnly(2026, 7, 4)).Workouts, x => x.WorkoutSessionId == SessionId);
        var august = await f.HistoryCalendar.GetCalendarMonthAsync(2026, 8);
        Assert.DoesNotContain(august.Days.SelectMany(x => x.Workouts), x => x.WorkoutSessionId == SessionId);
        var history = await f.HistoryCalendar.GetHistoryPageAsync(new(new(2026, 7, 4), new(2026, 7, 5), null, null, null, 25, null));
        Assert.Contains(history.Items, x => x.Id == SessionId && x.WorkoutDate == new DateOnly(2026, 7, 4));
        var stats = await f.Analytics.GetStatisticsAsync(new(new(new(2026, 7, 1), new(2026, 8, 1), null), ids.ExerciseId, StatisticsMetric.Volume));
        Assert.Contains(stats.Series.Points, x => x.Date == new DateOnly(2026, 7, 4));
        Assert.Equal(500m, stats.Series.Points.Single(x => x.Date == new DateOnly(2026, 7, 4)).Value);
    }

    private static async Task<(Guid ExerciseId, Guid SetId)> SeedCompletedAsync(DatabaseTestFixture f)
    {
        var muscle = new MuscleGroup(Guid.NewGuid(), "Chest", 0, Created);
        var exercise = new Exercise(Guid.NewGuid(), "Press", muscle.Id, null, Created);
        var menu = new WorkoutMenu(Guid.NewGuid(), "Push", null, Created);
        var session = new WorkoutSession(SessionId, new(2026, 8, 10), menu.Id, "Workout", "session memo", Created); session.Complete(Completed);
        var workoutExercise = new WorkoutExercise(Guid.NewGuid(), SessionId, exercise.Id, exercise.Name, muscle.Id, muscle.Name, 0, "exercise memo", Created);
        var set = new WorkoutSet(Guid.NewGuid(), workoutExercise.Id, 1, 50m, 10, 8m, Created);
        await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(muscle, exercise, menu, session, workoutExercise, set); await db.SaveChangesAsync();
        return (exercise.Id, set.Id);
    }

    private static async Task<SessionSnapshot> ReadSessionAsync(DatabaseTestFixture f)
    {
        await using var db = await f.Factory.CreateDbContextAsync();
        var session = await db.WorkoutSessions.Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleAsync(x => x.Id == SessionId);
        var exercise = session.Exercises.Single(); var set = exercise.Sets.Single();
        return new(session.WorkoutDate, session.CreatedAtUtc, session.CompletedAtUtc, session.UpdatedAtUtc, session.Name, session.Note, session.SourceMenuId,
            exercise.ExerciseId, exercise.ExerciseNameSnapshot, exercise.PrimaryMuscleNameSnapshot, exercise.Note, set.Id, set.WeightKg, set.Reps, set.Rpe);
    }

    private sealed record SessionSnapshot(DateOnly WorkoutDate, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, DateTimeOffset UpdatedAtUtc,
        string? Name, string? Note, Guid? SourceMenuId, Guid ExerciseId, string ExerciseNameSnapshot, string MuscleNameSnapshot, string? ExerciseNote,
        Guid SetId, decimal? WeightKg, int? Reps, decimal? Rpe);
}
