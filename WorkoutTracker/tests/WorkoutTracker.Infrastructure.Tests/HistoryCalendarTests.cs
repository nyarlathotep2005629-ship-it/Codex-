using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class HistoryCalendarTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 8, 22, 6, 0, 0, TimeSpan.Zero);

    [Fact(DisplayName = "HIS-001 History sort is WorkoutDate DESC CompletedAtUtc DESC Id TEXT DESC")]
    public async Task His001Sort()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f);
        var low = Id(1); var high = Id(2); await AddCompletedAsync(f, master, low, new DateOnly(2026, 8, 20), BaseTime); await AddCompletedAsync(f, master, high, new DateOnly(2026, 8, 20), BaseTime); var newestTime = Id(3); await AddCompletedAsync(f, master, newestTime, new DateOnly(2026, 8, 20), BaseTime.AddMinutes(1)); var newestDate = Id(4); await AddCompletedAsync(f, master, newestDate, new DateOnly(2026, 8, 21), BaseTime.AddDays(-1));
        var page = await f.HistoryCalendar.GetHistoryPageAsync(Query(25));
        Assert.Equal([newestDate, newestTime, high, low], page.Items.Select(x => x.Id));
    }

    [Fact(DisplayName = "HIS-002 HIS-003 and DB-017 Keyset pages have no duplicate or missing rows across 125 INTEGER TEXT cursors")]
    public async Task His002His003Db017()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); var expected = Enumerable.Range(1, 125).Select(Id).ToList(); await AddManyCompletedAsync(f, master, expected, new DateOnly(2026, 8, 20), BaseTime);
        var actual = await ReadAllAsync(f, Query(50));
        Assert.Equal(125, actual.Count); Assert.Equal(125, actual.Distinct().Count()); Assert.Equal(expected.OrderByDescending(x => x.ToString("D"), StringComparer.Ordinal), actual);
    }

    [Fact(DisplayName = "HIS-004 same WorkoutDate and CompletedAt use stable Id final tie breaker across pages")]
    public async Task His004TieBreaker()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); var expected = Enumerable.Range(1, 100).Select(Id).ToList(); await AddManyCompletedAsync(f, master, expected, new DateOnly(2026, 8, 20), BaseTime);
        var first = await ReadAllAsync(f, Query(25)); var second = await ReadAllAsync(f, Query(25)); Assert.Equal(first, second); Assert.Equal(expected.OrderByDescending(x => x.ToString("D"), StringComparer.Ordinal), first);
    }

    [Fact(DisplayName = "HIS-005 Completed-only History excludes Draft and never creates a null-time cursor")]
    public async Task His005CompletedOnly()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); await AddCompletedAsync(f, master, Id(1), new DateOnly(2026, 8, 20), BaseTime); await AddDraftAsync(f, master, Id(2), new DateOnly(2026, 8, 21));
        var page = await f.HistoryCalendar.GetHistoryPageAsync(Query(25)); Assert.Single(page.Items); Assert.Equal(Id(1), page.Items[0].Id); Assert.All(page.Items, x => Assert.Equal(TimeSpan.Zero, x.CompletedAtUtc.Offset));
    }

    [Fact(DisplayName = "HIS-006 History query plan uses keyset composite Index without temporary sort")]
    public async Task His006Index()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); await using var connection = new SqliteConnection($"Data Source={f.DatabasePath};Foreign Keys=True"); await connection.OpenAsync();
        await using (var analyze = connection.CreateCommand()) { analyze.CommandText = "ANALYZE"; await analyze.ExecuteNonQueryAsync(); }
        await using var command = connection.CreateCommand(); command.CommandText = "EXPLAIN QUERY PLAN SELECT Id FROM WorkoutSessions WHERE Status='Completed' ORDER BY WorkoutDate DESC, CompletedAtUtcTicks DESC, Id DESC LIMIT 51"; await using var reader = await command.ExecuteReaderAsync(); var details = new List<string>(); while (await reader.ReadAsync()) details.Add(reader.GetString(3)); var plan = string.Join(" | ", details);
        Assert.Contains("IX_WorkoutSessions_History_Keyset", plan); Assert.DoesNotContain("TEMP B-TREE", plan, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "HIS-009 HIS-010 HIS-011 HIS-012 combined filters use half-open period and Snapshot muscle before keyset pagination")]
    public async Task HistoryFilters()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var a = await SeedMasterAsync(f, "A", "Chest", "Menu A"); var b = await SeedMasterAsync(f, "B", "Back", "Menu B");
        await AddCompletedAsync(f, a, Id(1), new DateOnly(2026, 8, 1), BaseTime); await AddCompletedAsync(f, a, Id(2), new DateOnly(2026, 8, 31), BaseTime); await AddCompletedAsync(f, b, Id(3), new DateOnly(2026, 8, 15), BaseTime); await AddCompletedAsync(f, a, Id(4), new DateOnly(2026, 9, 1), BaseTime); await AddDraftAsync(f, a, Id(5), new DateOnly(2026, 8, 10));
        var combined = new HistoryQuery(new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), a.ExerciseId, a.MuscleId, a.MenuId, 25, null); var page = await f.HistoryCalendar.GetHistoryPageAsync(combined);
        Assert.Equal([Id(2), Id(1)], page.Items.Select(x => x.Id));
        Assert.Equal(3, (await f.HistoryCalendar.GetHistoryPageAsync(Query(25) with { From = new DateOnly(2026, 8, 1), To = new DateOnly(2026, 9, 1) })).Items.Count);
        Assert.Equal(3, (await f.HistoryCalendar.GetHistoryPageAsync(Query(25) with { ExerciseId = a.ExerciseId })).Items.Count);
        Assert.Equal(3, (await f.HistoryCalendar.GetHistoryPageAsync(Query(25) with { PrimaryMuscleId = a.MuscleId })).Items.Count);
        Assert.Equal(3, (await f.HistoryCalendar.GetHistoryPageAsync(Query(25) with { SourceMenuId = a.MenuId })).Items.Count);
    }

    [Fact(DisplayName = "HIS-012 filtered keyset keeps filter on every page")]
    public async Task FilteredPagination()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var a = await SeedMasterAsync(f, "A", "Chest", "A menu"); var b = await SeedMasterAsync(f, "B", "Back", "B menu");
        await AddManyCompletedAsync(f, a, Enumerable.Range(1, 60).Select(Id).ToList(), new DateOnly(2026, 8, 20), BaseTime); await AddManyCompletedAsync(f, b, Enumerable.Range(1001, 30).Select(Id).ToList(), new DateOnly(2026, 8, 20), BaseTime);
        var actual = await ReadAllAsync(f, Query(25) with { SourceMenuId = a.MenuId }); Assert.Equal(60, actual.Count); Assert.All(actual, id => Assert.True(int.Parse(id.ToString("N")[20..]) < 1000));
    }

    [Fact(DisplayName = "HIS-009 HIS-010 HIS-011 WPF History initialization path translates all filter options server-side")]
    public async Task HistoryFilterOptionsUseRealApplicationServiceAndSqliteQuery()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync();
        var z = await SeedMasterAsync(f, "Z Press", "Chest", "Z Menu");
        var a = await SeedMasterAsync(f, "A Curl", "Arms", "A Menu");
        var draftOnly = await SeedMasterAsync(f, "Draft Only", "Draft Muscle", "Draft Menu");
        await AddCompletedAsync(f, z, Id(1), new DateOnly(2026, 8, 20), BaseTime);
        await AddCompletedAsync(f, z, Id(2), new DateOnly(2026, 8, 21), BaseTime.AddMinutes(1));
        await AddCompletedAsync(f, a, Id(3), new DateOnly(2026, 8, 22), BaseTime.AddMinutes(2));
        await AddDraftAsync(f, draftOnly, Id(4), new DateOnly(2026, 8, 22));
        var counter = new CommandCounter();
        var options = new DbContextOptionsBuilder<WorkoutTrackerDbContext>()
            .UseSqlite($"Data Source={f.DatabasePath};Foreign Keys=True")
            .AddInterceptors(counter).Options;
        var service = new HistoryCalendarService(new HistoryCalendarQuery(new TestContextFactory(options)), new TaskSchedulerOffUiExecutor());

        var result = await service.GetHistoryFilterOptionsAsync();

        Assert.Equal(["A Curl", "Z Press"], result.Exercises.Select(x => x.Name));
        Assert.Equal(["Arms", "Chest"], result.PrimaryMuscles.Select(x => x.Name));
        Assert.Equal(["A Menu", "Z Menu"], result.Menus.Select(x => x.Name));
        Assert.Equal(3, counter.ReaderCount);
        Assert.Contains(counter.Commands, sql => sql.Contains("SELECT DISTINCT", StringComparison.OrdinalIgnoreCase)
            && sql.Contains("PrimaryMuscleIdSnapshot", StringComparison.Ordinal)
            && sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase));
        Assert.All(counter.Commands, sql => Assert.DoesNotContain("SELECT *", sql, StringComparison.OrdinalIgnoreCase));
        await using var connection = new SqliteConnection($"Data Source={f.DatabasePath};Foreign Keys=True"); await connection.OpenAsync();
        await using (var analyze = connection.CreateCommand()) { analyze.CommandText = "ANALYZE"; await analyze.ExecuteNonQueryAsync(); }
        await using var explain = connection.CreateCommand();
        explain.CommandText = "EXPLAIN QUERY PLAN SELECT DISTINCT we.PrimaryMuscleIdSnapshot, we.PrimaryMuscleNameSnapshot FROM WorkoutExercises we JOIN WorkoutSessions s ON s.Id=we.WorkoutSessionId WHERE s.Status='Completed' ORDER BY we.PrimaryMuscleNameSnapshot, we.PrimaryMuscleIdSnapshot";
        await using var reader = await explain.ExecuteReaderAsync(); var plan = new List<string>(); while (await reader.ReadAsync()) plan.Add(reader.GetString(3));
        var planText = string.Join(" | ", plan); Assert.Contains("SEARCH", planText, StringComparison.OrdinalIgnoreCase); Assert.Contains("INDEX", planText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact(DisplayName = "CAL-001 monthly query includes month boundaries year boundary and leap day only")]
    public async Task Cal001Boundaries()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); await AddCompletedAsync(f, master, Id(1), new DateOnly(2024, 1, 31), BaseTime); await AddCompletedAsync(f, master, Id(2), new DateOnly(2024, 2, 1), BaseTime); await AddCompletedAsync(f, master, Id(3), new DateOnly(2024, 2, 29), BaseTime); await AddCompletedAsync(f, master, Id(4), new DateOnly(2024, 3, 1), BaseTime); await AddCompletedAsync(f, master, Id(5), new DateOnly(2025, 1, 1), BaseTime);
        var counter = new CommandCounter(); var options = new DbContextOptionsBuilder<WorkoutTrackerDbContext>().UseSqlite($"Data Source={f.DatabasePath};Foreign Keys=True").AddInterceptors(counter).Options; var countedQuery = new HistoryCalendarQuery(new TestContextFactory(options));
        var february = await countedQuery.GetCalendarMonthAsync(2024, 2); Assert.Equal(1, counter.ReaderCount); Assert.Equal(29, february.Days.Count); Assert.Equal([Id(2)], february.Days.Single(x => x.Date == new DateOnly(2024, 2, 1)).Workouts.Select(x => x.WorkoutSessionId)); Assert.Equal([Id(3)], february.Days.Single(x => x.Date == new DateOnly(2024, 2, 29)).Workouts.Select(x => x.WorkoutSessionId)); Assert.Equal(2, february.Days.Sum(x => x.WorkoutCount));
        var january = await f.HistoryCalendar.GetCalendarMonthAsync(2025, 1); Assert.Equal(Id(5), january.Days.Single(x => x.Date == new DateOnly(2025, 1, 1)).Workouts.Single().WorkoutSessionId);
    }

    [Fact(DisplayName = "CAL-002 Calendar markers contain Completed only and exclude Draft")]
    public async Task Cal002CompletedOnly()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); await AddCompletedAsync(f, master, Id(1), new DateOnly(2026, 8, 10), BaseTime); await AddDraftAsync(f, master, Id(2), new DateOnly(2026, 8, 10));
        var month = await f.HistoryCalendar.GetCalendarMonthAsync(2026, 8); var day = month.Days.Single(x => x.Date == new DateOnly(2026, 8, 10)); Assert.Single(day.Workouts); Assert.Equal(Id(1), day.Workouts[0].WorkoutSessionId);
    }

    [Fact(DisplayName = "CAL-003 selected-day DTO contains every Workout Menu Sets Volume and muscles")]
    public async Task Cal003DaySummary()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f, "Bench", "Chest", "Push"); await AddCompletedAsync(f, master, Id(1), new DateOnly(2026, 8, 10), BaseTime, 50m, 10); await AddCompletedAsync(f, master, Id(2), new DateOnly(2026, 8, 10), BaseTime.AddMinutes(1), 100m, 5);
        var day = (await f.HistoryCalendar.GetCalendarMonthAsync(2026, 8)).Days.Single(x => x.Date == new DateOnly(2026, 8, 10)); Assert.Equal(2, day.WorkoutCount); Assert.All(day.Workouts, x => { Assert.Equal("Push", x.SourceMenuName); Assert.Equal(500m, x.TotalVolume); Assert.Equal(1, x.TotalSets); Assert.Contains("Chest", x.PrimaryMuscles); });
    }

    [Fact(DisplayName = "CAL-004 common Workout Detail returns Session Snapshot Exercise Set Note and summary")]
    public async Task Cal004Detail()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f, "Bench", "Chest", "Push"); await AddCompletedAsync(f, master, Id(1), new DateOnly(2026, 8, 10), BaseTime, 75m, 8, "session note", "exercise note");
        var detail = await f.HistoryCalendar.GetWorkoutDetailAsync(Id(1)); Assert.NotNull(detail); Assert.Equal("session note", detail.Note); Assert.Equal("Bench", detail.Exercises.Single().ExerciseNameSnapshot); Assert.Equal("exercise note", detail.Exercises.Single().Note); Assert.Equal(75m, detail.Exercises.Single().Sets.Single().WeightKg); Assert.Equal(600m, detail.TotalVolume); Assert.Equal(1, detail.TotalSets);
    }

    private static HistoryQuery Query(int pageSize) => new(null, null, null, null, null, pageSize, null);
    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static async Task<List<Guid>> ReadAllAsync(DatabaseTestFixture f, HistoryQuery template)
    {
        var ids = new List<Guid>(); HistoryCursor? cursor = null; do { var page = await f.HistoryCalendar.GetHistoryPageAsync(template with { Cursor = cursor }); ids.AddRange(page.Items.Select(x => x.Id)); cursor = page.NextCursor; } while (cursor is not null); return ids;
    }
    private static async Task<Master> SeedMasterAsync(DatabaseTestFixture f, string exerciseName = "Exercise", string muscleName = "Muscle", string menuName = "Menu")
    {
        var muscle = await f.MasterData.SaveMuscleGroupAsync(new(null, muscleName, 0), BaseTime); var exercise = await f.MasterData.SaveExerciseAsync(new(null, exerciseName, muscle.Id, [], null), BaseTime); var menu = await f.MasterData.SaveMenuAsync(new(null, menuName, null, [new(null, exercise.Id, 1, null)]), BaseTime); return new(muscle.Id, exercise.Id, menu.Id, exerciseName, muscleName);
    }
    private static async Task AddCompletedAsync(DatabaseTestFixture f, Master master, Guid id, DateOnly date, DateTimeOffset completedAt, decimal weight = 50m, int reps = 10, string? sessionNote = null, string? exerciseNote = null)
    {
        var session = new WorkoutSession(id, date, master.MenuId, "Workout", sessionNote, completedAt.AddMinutes(-1)); session.Complete(completedAt); var exercise = new WorkoutExercise(Guid.NewGuid(), id, master.ExerciseId, master.ExerciseName, master.MuscleId, master.MuscleName, 0, exerciseNote, completedAt.AddMinutes(-1)); var set = new WorkoutSet(Guid.NewGuid(), exercise.Id, 1, weight, reps, 8m, completedAt.AddMinutes(-1)); await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(session, exercise, set); await db.SaveChangesAsync();
    }
    private static async Task AddManyCompletedAsync(DatabaseTestFixture f, Master master, IReadOnlyList<Guid> ids, DateOnly date, DateTimeOffset completedAt)
    {
        await using var db = await f.Factory.CreateDbContextAsync(); foreach (var id in ids) { var session = new WorkoutSession(id, date, master.MenuId, null, null, completedAt.AddMinutes(-1)); session.Complete(completedAt); var exercise = new WorkoutExercise(Guid.NewGuid(), id, master.ExerciseId, master.ExerciseName, master.MuscleId, master.MuscleName, 0, null, completedAt.AddMinutes(-1)); db.AddRange(session, exercise, new WorkoutSet(Guid.NewGuid(), exercise.Id, 1, 10m, 10, null, completedAt.AddMinutes(-1))); } await db.SaveChangesAsync();
    }
    private static async Task AddDraftAsync(DatabaseTestFixture f, Master master, Guid id, DateOnly date)
    {
        var session = new WorkoutSession(id, date, master.MenuId, "Draft", null, BaseTime); var exercise = new WorkoutExercise(Guid.NewGuid(), id, master.ExerciseId, master.ExerciseName, master.MuscleId, master.MuscleName, 0, null, BaseTime); await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(session, exercise, new WorkoutSet(Guid.NewGuid(), exercise.Id, 1, null, null, null, BaseTime)); await db.SaveChangesAsync();
    }
    private sealed record Master(Guid MuscleId, Guid ExerciseId, Guid MenuId, string ExerciseName, string MuscleName);
    private sealed class TestContextFactory(DbContextOptions<WorkoutTrackerDbContext> options) : IDbContextFactory<WorkoutTrackerDbContext> { public WorkoutTrackerDbContext CreateDbContext() => new(options); }
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int ReaderCount { get; private set; }
        public List<string> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { ReaderCount++; Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }
}
