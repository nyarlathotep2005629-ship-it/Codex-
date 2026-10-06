using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class PersistenceTests
{
    [Fact(DisplayName = "DB-004 Connection string enables Foreign Keys")]
    public void Db004()
    {
        var builder = new SqliteConnectionStringBuilder(SqliteConnectionStringFactory.Create("test.db"));
        Assert.True(builder.ForeignKeys);
    }

    [Fact(DisplayName = "DB-010 Fresh database migrates to initial schema")]
    public async Task Db010()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(9L, await ScalarAsync<long>(db, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name IN ('MuscleGroups','Exercises','ExerciseSecondaryMuscles','WorkoutMenus','MenuExercises','WorkoutSessions','WorkoutExercises','WorkoutSets','AppSettings');"));
        Assert.Equal(1L, await ScalarAsync<long>(db, "PRAGMA user_version;"));
    }

    [Fact(DisplayName = "DB-001 WeightHundredthsKg persists exactly as INTEGER")]
    public async Task Db001()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        var ids = await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 20), 100.01m);
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Equal(10001L, await ScalarAsync<long>(db, "SELECT WeightHundredthsKg FROM WorkoutSets;"));
        Assert.Equal(100.01m, (await db.WorkoutSets.AsNoTracking().SingleAsync()).WeightKg);
    }

    [Fact(DisplayName = "DB-002 Weight INTEGER filtering and sorting is numeric")]
    public async Task Db002()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 18), 9.99m); await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 19), 100m); await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 20), 20m);
        await using var db = await f.Factory.CreateDbContextAsync();
        var values = await db.WorkoutSets.AsNoTracking().OrderBy(x => x.WeightKg).Select(x => x.WeightKg).ToListAsync();
        Assert.Equal([9.99m, 20m, 100m], values); Assert.Equal(2L, await ScalarAsync<long>(db, "SELECT count(*) FROM WorkoutSets WHERE WeightHundredthsKg >= 2000;"));
    }

    [Fact(DisplayName = "DB-003 Weight integer aggregate uses 64-bit sum and exact decimal conversion")]
    public async Task Db003()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 19), 100m); await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 20), 80.50m);
        await using var db = await f.Factory.CreateDbContextAsync();
        var hundredthsVolume = await ScalarAsync<long>(db, "SELECT SUM(WeightHundredthsKg * Reps) FROM WorkoutSets;");
        Assert.Equal(90250L, hundredthsVolume); Assert.Equal(902.50m, hundredthsVolume / 100m);
    }

    [Fact(DisplayName = "DB-015 UTC ticks and DB-016 DayNumber persist as INTEGER")]
    public async Task Db015Db016()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        var date = new DateOnly(2026, 8, 20); var completed = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        await SeedCompletedGraphAsync(f, date, 100m, completed);
        await using var db = await f.Factory.CreateDbContextAsync();
        Assert.Equal(date.DayNumber, await ScalarAsync<long>(db, "SELECT WorkoutDate FROM WorkoutSessions;"));
        Assert.Equal(completed.UtcTicks, await ScalarAsync<long>(db, "SELECT CompletedAtUtcTicks FROM WorkoutSessions;"));
    }

    [Fact(DisplayName = "DB-005 Every factory connection has FK enforcement and DB-013 contexts are short-lived")]
    public async Task Db005Db013()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await using var first = await f.Factory.CreateDbContextAsync(); await using var second = await f.Factory.CreateDbContextAsync();
        Assert.NotSame(first, second);
        Assert.Equal(1L, await ScalarAsync<long>(first, "PRAGMA foreign_keys;"));
        Assert.Equal(1L, await ScalarAsync<long>(second, "PRAGMA foreign_keys;"));
    }

    [Fact(DisplayName = "APP-006 and APP-007 database allows at most one Draft")]
    public async Task App006App007()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        await f.Transactions.ExecuteAsync(ct => f.Sessions.AddAsync(new WorkoutSession(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, null, null, now), ct));
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => f.Transactions.ExecuteAsync(ct => f.Sessions.AddAsync(new WorkoutSession(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, null, null, now), ct)));
        Assert.Contains("UNIQUE", ex.InnerException?.Message ?? ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "DB-006 invalid FK is rejected and DB-007 workout graph cascades")]
    public async Task Db006Db007()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await using (var db = await f.Factory.CreateDbContextAsync())
        {
            db.Exercises.Add(new Exercise(Guid.NewGuid(), "Invalid", Guid.NewGuid(), null, DateTimeOffset.UtcNow));
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
        var ids = await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 20), 100m);
        await using var deleteDb = await f.Factory.CreateDbContextAsync();
        var session = await deleteDb.WorkoutSessions.SingleAsync(x => x.Id == ids.SessionId); deleteDb.Remove(session); await deleteDb.SaveChangesAsync();
        Assert.Equal(0L, await ScalarAsync<long>(deleteDb, "SELECT count(*) FROM WorkoutExercises;") + await ScalarAsync<long>(deleteDb, "SELECT count(*) FROM WorkoutSets;"));
    }

    [Fact(DisplayName = "DB-014 transaction coordinator rolls back the entire write operation")]
    public async Task Db014()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Transactions.ExecuteAsync(async ct =>
        {
            await f.Sessions.AddAsync(new WorkoutSession(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, null, null, DateTimeOffset.UtcNow), ct);
            throw new InvalidOperationException("fault injection");
        }));
        await using var db = await f.Factory.CreateDbContextAsync(); Assert.Equal(0, await db.WorkoutSessions.CountAsync());
    }

    [Fact(DisplayName = "DB-018 muscle snapshot has no FK and survives current muscle deletion")]
    public async Task Db018()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var now = DateTimeOffset.UtcNow;
        var oldMuscle = new MuscleGroup(Guid.NewGuid(), "Old", 0, now); var currentMuscle = new MuscleGroup(Guid.NewGuid(), "Current", 1, now);
        var exercise = new Exercise(Guid.NewGuid(), "Exercise", oldMuscle.Id, null, now); var session = new WorkoutSession(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), null, null, null, now); session.Complete(now);
        var workoutExercise = new WorkoutExercise(Guid.NewGuid(), session.Id, exercise.Id, exercise.Name, oldMuscle.Id, oldMuscle.Name, 0, null, now);
        await using (var db = await f.Factory.CreateDbContextAsync()) { db.AddRange(oldMuscle, currentMuscle, exercise, session, workoutExercise); await db.SaveChangesAsync(); await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE Exercises SET PrimaryMuscleId={currentMuscle.Id} WHERE Id={exercise.Id}"); }
        await using (var db = await f.Factory.CreateDbContextAsync()) { db.Remove(await db.MuscleGroups.SingleAsync(x => x.Id == oldMuscle.Id)); await db.SaveChangesAsync(); }
        await using var verify = await f.Factory.CreateDbContextAsync(); var snapshot = await verify.WorkoutExercises.AsNoTracking().SingleAsync(); Assert.Equal(oldMuscle.Id, snapshot.PrimaryMuscleIdSnapshot); Assert.Equal("Old", snapshot.PrimaryMuscleNameSnapshot);
    }

    [Fact(DisplayName = "DB-008 deleting a source menu sets historical SourceMenuId to null")]
    public async Task Db008()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var now = DateTimeOffset.UtcNow;
        var menu = new WorkoutMenu(Guid.NewGuid(), "Menu", null, now);
        var session = new WorkoutSession(Guid.NewGuid(), DateOnly.FromDateTime(DateTime.Today), menu.Id, null, null, now); session.Complete(now);
        await using (var db = await f.Factory.CreateDbContextAsync()) { db.AddRange(menu, session); await db.SaveChangesAsync(); }
        await using (var db = await f.Factory.CreateDbContextAsync()) { db.Remove(await db.WorkoutMenus.SingleAsync()); await db.SaveChangesAsync(); }
        await using var verify = await f.Factory.CreateDbContextAsync(); Assert.Null((await verify.WorkoutSessions.AsNoTracking().SingleAsync()).SourceMenuId);
    }

    [Fact(DisplayName = "DB-009 required indexes and one-Draft partial index exist")]
    public async Task Db009()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); await using var db = await f.Factory.CreateDbContextAsync();
        var sql = await ScalarAsync<string>(db, "SELECT sql FROM sqlite_master WHERE type='index' AND name='UX_WorkoutSessions_OneDraft';");
        Assert.Contains("UNIQUE", sql, StringComparison.OrdinalIgnoreCase); Assert.Contains("WHERE Status = 'Draft'", sql, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await ScalarAsync<string>(db, "SELECT sql FROM sqlite_master WHERE type='index' AND name='IX_WorkoutSessions_History_Keyset';"));
        Assert.Equal(0L, await ScalarAsync<long>(db, "SELECT count(*) FROM pragma_foreign_key_list('WorkoutExercises') WHERE \"from\"='PrimaryMuscleIdSnapshot';"));
    }

    [Fact(DisplayName = "HIS-001 repository keyset sort smoke test")]
    public async Task His001To008()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 22), 100m, new DateTimeOffset(2026, 8, 22, 10, 0, 0, TimeSpan.Zero));
        await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 21), 90m, new DateTimeOffset(2026, 8, 21, 10, 0, 0, TimeSpan.Zero));
        await SeedCompletedGraphAsync(f, new DateOnly(2026, 8, 20), 80m, new DateTimeOffset(2026, 8, 20, 10, 0, 0, TimeSpan.Zero));
        var first = await f.Sessions.GetHistoryPageAsync(new(null, null, null, null, null, 2, null));
        var second = await f.Sessions.GetHistoryPageAsync(new(null, null, null, null, null, 2, first.NextCursor));
        Assert.Equal(2, first.Items.Count); Assert.Single(second.Items); Assert.Empty(first.Items.Select(x => x.Id).Intersect(second.Items.Select(x => x.Id)));
        Assert.True(first.Items[0].WorkoutDate > first.Items[1].WorkoutDate);
    }

    private static async Task<(Guid SessionId, Guid SetId)> SeedCompletedGraphAsync(DatabaseTestFixture f, DateOnly date, decimal weight, DateTimeOffset? completed = null)
    {
        var now = completed ?? new DateTimeOffset(date.Year, date.Month, date.Day, 12, 0, 0, TimeSpan.Zero);
        var muscle = new MuscleGroup(Guid.NewGuid(), $"Muscle-{Guid.NewGuid():N}", 0, now); var exercise = new Exercise(Guid.NewGuid(), $"Exercise-{Guid.NewGuid():N}", muscle.Id, null, now);
        var session = new WorkoutSession(Guid.NewGuid(), date, null, null, null, now); session.Complete(now);
        var workoutExercise = new WorkoutExercise(Guid.NewGuid(), session.Id, exercise.Id, exercise.Name, muscle.Id, muscle.Name, 0, null, now);
        var set = new WorkoutSet(Guid.NewGuid(), workoutExercise.Id, 1, weight, 5, 8m, now);
        await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(muscle, exercise, session, workoutExercise, set); await db.SaveChangesAsync();
        return (session.Id, set.Id);
    }

    private static async Task<T> ScalarAsync<T>(WorkoutTrackerDbContext db, string sql)
    {
        var connection = db.Database.GetDbConnection(); if (connection.State != System.Data.ConnectionState.Open) await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; return (T)Convert.ChangeType((await command.ExecuteScalarAsync())!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
