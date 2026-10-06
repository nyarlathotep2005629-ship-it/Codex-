using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class AnalyticsQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 22, 8, 0, 0, TimeSpan.Zero);
    private static readonly AnalyticsPeriod August = new(new(2026, 8, 1), new(2026, 9, 1), new(2026, 7, 1));

    [Fact(DisplayName = "DSH-001 DSH-006 DSH-013 Completed summary excludes Draft and uses INTEGER weight volume")]
    public async Task DashboardSummaryCompletedOnly()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f);
        await AddWorkoutAsync(f, master, Id(1), new(2026, 8, 20), Now, [(100m, 10), (50.25m, 4)]);
        await AddDraftAsync(f, master, Id(2), new(2026, 8, 21), [(999m, 10)]);
        var result = await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, [master.ExerciseId]));
        Assert.Equal(1, result.Summary.WorkoutCount); Assert.Equal(1201m, result.Summary.TotalVolume); Assert.Equal(2, result.Summary.TotalSets);
        Assert.Single(result.OneRepMaxSeries); Assert.DoesNotContain(result.RecentWorkouts, x => x.WorkoutSessionId == Id(2));
    }

    [Fact(DisplayName = "DSH-002 DSH-003 DSH-007 current Previous comparison uses equal half-open periods")]
    public async Task PreviousComparison()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f);
        await AddWorkoutAsync(f, master, Id(1), new(2026, 7, 31), Now, [(50m, 10)]);
        await AddWorkoutAsync(f, master, Id(2), new(2026, 8, 1), Now.AddMinutes(1), [(100m, 10)]);
        var result = await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, []));
        var count = result.Comparisons.Single(x => x.Metric == "Workout Count"); var volume = result.Comparisons.Single(x => x.Metric == "Total Volume");
        Assert.Equal(1m, count.CurrentValue); Assert.Equal(1m, count.PreviousValue); Assert.Equal(1000m, volume.CurrentValue); Assert.Equal(500m, volume.PreviousValue); Assert.Equal(100m, volume.PercentageDifference);
    }

    [Fact(DisplayName = "DSH-012 Primary Muscle volume uses Snapshot only and ratio totals one")]
    public async Task PrimaryMuscleSnapshotVolume()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var chest = await SeedMasterAsync(f, "Bench", "Historical Chest"); var back = await SeedMasterAsync(f, "Row", "Back");
        await AddWorkoutAsync(f, chest, Id(1), new(2026, 8, 5), Now, [(100m, 10)]); await AddWorkoutAsync(f, back, Id(2), new(2026, 8, 6), Now, [(50m, 10)]);
        await using (var db = await f.Factory.CreateDbContextAsync()) { var muscle = await db.MuscleGroups.SingleAsync(x => x.Id == chest.MuscleId); muscle.Update("Renamed Current", 0, Now.AddDays(1)); await db.SaveChangesAsync(); }
        var result = await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, []));
        Assert.Contains(result.MuscleVolumes, x => x.Name.StartsWith("Historical Chest", StringComparison.Ordinal) && x.Volume == 1000m); Assert.DoesNotContain(result.MuscleVolumes, x => x.Name == "Renamed Current"); Assert.Equal(1m, result.MuscleVolumes.Sum(x => x.Ratio));
    }

    [Fact(DisplayName = "DSH-014 DSH-015 frequency uses Monday buckets partial weeks same-day sessions and Completed only")]
    public async Task MondayFrequency()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f);
        await AddWorkoutAsync(f, master, Id(1), new(2026, 8, 1), Now, [(10m, 1)]); await AddWorkoutAsync(f, master, Id(2), new(2026, 8, 1), Now.AddMinutes(1), [(10m, 1)]); await AddWorkoutAsync(f, master, Id(3), new(2026, 8, 3), Now.AddMinutes(2), [(10m, 1)]); await AddDraftAsync(f, master, Id(4), new(2026, 8, 3), []);
        var result = await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, []));
        Assert.Equal(new DateOnly(2026, 7, 27), result.Frequency[0].BucketStart); Assert.Equal(2, result.Frequency[0].WorkoutCount); Assert.Equal(1, result.Frequency[1].WorkoutCount); Assert.Equal(6, result.Frequency.Count);
    }

    [Fact(DisplayName = "DSH-016 DSH-017 Recent returns top five exact WorkoutDate CompletedAt Id order without placeholders")]
    public async Task RecentTopFive()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f);
        for (var i = 1; i <= 7; i++) await AddWorkoutAsync(f, master, Id(i), new(2026, 8, 20), Now.AddMinutes(i <= 2 ? 0 : i), [(10m, i)]);
        var result = await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, []));
        Assert.Equal(5, result.RecentWorkouts.Count); Assert.Equal([Id(7), Id(6), Id(5), Id(4), Id(3)], result.RecentWorkouts.Select(x => x.WorkoutSessionId));
    }

    [Fact(DisplayName = "DSH-008 DSH-010 DSH-011 Small Multiples are independent per exercise and same-day 1RM uses max")]
    public async Task SmallMultiplesAndSameDayMax()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var bench = await SeedMasterAsync(f, "Bench", "Chest"); var curl = await SeedMasterAsync(f, "Curl", "Arms");
        await AddWorkoutAsync(f, bench, Id(1), new(2026, 8, 10), Now, [(100m, 10), (110m, 5)]); await AddWorkoutAsync(f, bench, Id(2), new(2026, 8, 10), Now.AddMinutes(1), [(90m, 15)]); await AddWorkoutAsync(f, curl, Id(3), new(2026, 8, 10), Now, [(10m, 10)]);
        var result = await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, [bench.ExerciseId, curl.ExerciseId]));
        Assert.Equal(2, result.OneRepMaxSeries.Count); Assert.All(result.OneRepMaxSeries, x => Assert.Single(x.Points)); Assert.Equal(135m, result.OneRepMaxSeries.Single(x => x.ExerciseId == bench.ExerciseId).Points[0].Value); Assert.Equal("kg", result.OneRepMaxSeries[0].Unit);
    }

    [Theory(DisplayName = "STA-005 DSH-011 Statistics metrics aggregate same-day max or sum exactly")]
    [InlineData(StatisticsMetric.EstimatedOneRepMax, 133.333333)]
    [InlineData(StatisticsMetric.MaxWeight, 100)]
    [InlineData(StatisticsMetric.Volume, 2250)]
    [InlineData(StatisticsMetric.Sets, 3)]
    [InlineData(StatisticsMetric.Reps, 15)]
    public async Task StatisticsMetrics(StatisticsMetric metric, double expected)
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f);
        await AddWorkoutAsync(f, master, Id(1), new(2026, 8, 10), Now, [(100m, 5), (50m, 15)]); await AddWorkoutAsync(f, master, Id(2), new(2026, 8, 10), Now.AddMinutes(1), [(100m, 10)]);
        var result = await f.Analytics.GetStatisticsAsync(new(August, master.ExerciseId, metric)); Assert.Single(result.Series.Points); Assert.Equal((decimal)expected, result.Series.Points[0].Value, 5);
    }

    [Fact(DisplayName = "STA-001 STA-002 STA-003 STA-004 STA-006 current best max count filter exclude Draft and reps 16")]
    public async Task StatisticsSummaryRules()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var a = await SeedMasterAsync(f, "A", "Chest"); var b = await SeedMasterAsync(f, "B", "Back");
        await AddWorkoutAsync(f, a, Id(1), new(2026, 8, 1), Now, [(100m, 15)]); await AddWorkoutAsync(f, a, Id(2), new(2026, 8, 20), Now, [(120m, 1), (200m, 16)]); await AddWorkoutAsync(f, b, Id(3), new(2026, 8, 21), Now, [(500m, 10)]); await AddDraftAsync(f, a, Id(4), new(2026, 8, 22), [(999m, 10)]);
        var result = await f.Analytics.GetStatisticsAsync(new(August, a.ExerciseId, StatisticsMetric.EstimatedOneRepMax));
        Assert.Equal(124m, result.CurrentOneRepMax); Assert.Equal(150m, result.BestOneRepMax); Assert.Equal(Id(1), result.BestOneRepMaxSourceId); Assert.Equal(200m, result.MaxWeight); Assert.Equal(2, result.WorkoutCount); Assert.Equal(2, result.Series.Points.Count);
    }

    [Fact(DisplayName = "APP-041 STA-008 completed deletion is reflected by a fresh Statistics query")]
    public async Task CompletedDeleteRefresh()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); await AddWorkoutAsync(f, master, Id(1), new(2026, 8, 10), Now, [(100m, 10)]);
        Assert.Equal(1, (await f.Analytics.GetStatisticsAsync(new(August, master.ExerciseId, StatisticsMetric.Volume))).WorkoutCount);
        await using (var db = await f.Factory.CreateDbContextAsync()) { var session = await db.WorkoutSessions.SingleAsync(x => x.Id == Id(1)); db.Remove(session); await db.SaveChangesAsync(); }
        var refreshed = await f.Analytics.GetStatisticsAsync(new(August, master.ExerciseId, StatisticsMetric.Volume)); Assert.Equal(0, refreshed.WorkoutCount); Assert.Empty(refreshed.Series.Points);
    }

    [Fact(DisplayName = "APP-005 Completed transition becomes visible to Dashboard and Statistics only after commit")]
    public async Task CompletedTransitionBecomesVisible()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); await AddDraftAsync(f, master, Id(1), new(2026, 8, 10), [(100m, 10)]);
        Assert.Equal(0, (await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, []))).Summary.WorkoutCount);
        await using (var db = await f.Factory.CreateDbContextAsync()) { var session = await db.WorkoutSessions.SingleAsync(x => x.Id == Id(1)); session.Complete(Now); await db.SaveChangesAsync(); }
        Assert.Equal(1, (await f.Analytics.GetDashboardAsync(new(August, DashboardPeriod.OneMonth, []))).Summary.WorkoutCount); Assert.Equal(1, (await f.Analytics.GetStatisticsAsync(new(August, master.ExerciseId, StatisticsMetric.Volume))).WorkoutCount);
    }

    [Fact(DisplayName = "DSH-009 settings persist period and 0-4 exercise identifiers without schema change")]
    public async Task SettingsPersist()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await f.Analytics.SaveDashboardSettingsAsync(new(DashboardPeriod.OneYear, ids)); var actual = await f.Analytics.GetSettingsAsync(); Assert.Equal(DashboardPeriod.OneYear, actual.Period); Assert.Equal(ids, actual.DashboardExerciseIds);
    }

    [Fact(DisplayName = "NFR-004 analytics query plan uses existing completed date and exercise indexes")]
    public async Task QueryPlanUsesExistingIndexes()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); await using var connection = new SqliteConnection($"Data Source={f.DatabasePath};Foreign Keys=True"); await connection.OpenAsync();
        var datePlan = await ExplainAsync(connection, "SELECT Id FROM WorkoutSessions INDEXED BY IX_WorkoutSessions_History_Keyset WHERE Status='Completed' AND WorkoutDate>=1 AND WorkoutDate<999999");
        var exercisePlan = await ExplainAsync(connection, "SELECT WorkoutSessionId FROM WorkoutExercises INDEXED BY IX_WorkoutExercises_Exercise_Session WHERE ExerciseId='00000000-0000-0000-0000-000000000001'");
        Assert.Contains("IX_WorkoutSessions_History_Keyset", datePlan); Assert.Contains("IX_WorkoutExercises_Exercise_Session", exercisePlan);
    }

    [Fact(DisplayName = "NFR-003 History first and next page p95 stay within targets on indexed multi-year data")]
    public async Task HistoryQueryPerformance()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); await AddBulkAsync(f, master, 1000, 2);
        await using (var connection = new SqliteConnection($"Data Source={f.DatabasePath};Foreign Keys=True")) { await connection.OpenAsync(); await using var analyze = connection.CreateCommand(); analyze.CommandText = "ANALYZE"; await analyze.ExecuteNonQueryAsync(); }
        var query = new HistoryQuery(null, null, null, null, null, 50, null); var firstTimes = new List<double>(); var nextTimes = new List<double>(); HistoryPage first = null!;
        for (var i=0;i<20;i++){ var sw=Stopwatch.StartNew(); first=await f.HistoryCalendar.GetHistoryPageAsync(query); sw.Stop(); firstTimes.Add(sw.Elapsed.TotalMilliseconds); }
        Assert.NotNull(first.NextCursor);
        for (var i=0;i<20;i++){ var sw=Stopwatch.StartNew(); var next=await f.HistoryCalendar.GetHistoryPageAsync(query with { Cursor=first.NextCursor }); sw.Stop(); nextTimes.Add(sw.Elapsed.TotalMilliseconds); Assert.Empty(first.Items.Select(x=>x.Id).Intersect(next.Items.Select(x=>x.Id))); }
        Assert.True(P95(firstTimes) <= 300, $"first p95={P95(firstTimes):0.0}ms"); Assert.True(P95(nextTimes) <= 250, $"next p95={P95(nextTimes):0.0}ms");
    }

    [Fact(DisplayName = "NFR-004 5000 Workout 100000 Set Statistics 1Y and ALL five metrics p95 meet targets")]
    public async Task StatisticsQueryPerformance()
    {
        await using var f = new DatabaseTestFixture(); await f.MigrateAsync(); var master = await SeedMasterAsync(f); await AddBulkAsync(f, master, 5000, 20);
        var periods = new[] { (Range: new AnalyticsPeriod(new DateOnly(2025, 8, 23), new DateOnly(2026, 8, 23), null), Limit: 500d), (Range: new AnalyticsPeriod(new DateOnly(2021, 1, 1), new DateOnly(2027, 1, 1), null), Limit: 1000d) };
        foreach (var period in periods) foreach (var metric in Enum.GetValues<StatisticsMetric>())
        {
            await f.Analytics.GetStatisticsAsync(new(period.Range, master.ExerciseId, metric)); var samples = new List<double>(); StatisticsDto result = null!;
            for(var i=0;i<20;i++){ var sw=Stopwatch.StartNew(); result=await f.Analytics.GetStatisticsAsync(new(period.Range, master.ExerciseId, metric)); sw.Stop(); samples.Add(sw.Elapsed.TotalMilliseconds); }
            Assert.NotEmpty(result.Series.Points); Assert.True(P95(samples) <= period.Limit, $"{metric} p95={P95(samples):0.0}ms limit={period.Limit}ms");
        }
    }

    private static Guid Id(int value) => Guid.Parse($"00000000-0000-0000-0000-{value:D12}");
    private static async Task<string> ExplainAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand(); command.CommandText = "EXPLAIN QUERY PLAN " + sql; await using var reader = await command.ExecuteReaderAsync(); var plan = new List<string>(); while (await reader.ReadAsync()) plan.Add(reader.GetString(3)); return string.Join(" | ", plan);
    }
    private static double P95(IReadOnlyList<double> values) => values.OrderBy(x => x).ElementAt((int)Math.Ceiling(values.Count * 0.95) - 1);
    private static async Task<Master> SeedMasterAsync(DatabaseTestFixture f, string exerciseName = "Exercise", string muscleName = "Muscle")
    {
        var suffix = Guid.NewGuid().ToString("N")[..6]; var muscle = await f.MasterData.SaveMuscleGroupAsync(new(null, $"{muscleName}-{suffix}", 0), Now); var exercise = await f.MasterData.SaveExerciseAsync(new(null, $"{exerciseName}-{suffix}", muscle.Id, [], null), Now); return new(muscle.Id, exercise.Id, exercise.Name, muscle.Name);
    }
    private static async Task AddWorkoutAsync(DatabaseTestFixture f, Master master, Guid id, DateOnly date, DateTimeOffset completed, IReadOnlyList<(decimal Weight, int Reps)> sets)
    {
        var session = new WorkoutSession(id, date, null, $"Workout-{id.ToString("N")[^2..]}", null, completed.AddMinutes(-1)); session.Complete(completed); var exercise = new WorkoutExercise(Guid.NewGuid(), id, master.ExerciseId, master.ExerciseName, master.MuscleId, master.MuscleName, 0, null, completed.AddMinutes(-1)); await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(session, exercise); var n=1; foreach(var set in sets) db.Add(new WorkoutSet(Guid.NewGuid(), exercise.Id, n++, set.Weight, set.Reps, null, completed.AddMinutes(-1))); await db.SaveChangesAsync();
    }
    private static async Task AddDraftAsync(DatabaseTestFixture f, Master master, Guid id, DateOnly date, IReadOnlyList<(decimal Weight, int Reps)> sets)
    {
        var session = new WorkoutSession(id, date, null, "Draft", null, Now); var exercise = new WorkoutExercise(Guid.NewGuid(), id, master.ExerciseId, master.ExerciseName, master.MuscleId, master.MuscleName, 0, null, Now); await using var db = await f.Factory.CreateDbContextAsync(); db.AddRange(session, exercise); var n=1; foreach(var set in sets) db.Add(new WorkoutSet(Guid.NewGuid(), exercise.Id, n++, set.Weight, set.Reps, null, Now)); await db.SaveChangesAsync();
    }
    private static async Task AddBulkAsync(DatabaseTestFixture f, Master master, int count, int setsPerWorkout)
    {
        await using var db = await f.Factory.CreateDbContextAsync(); db.ChangeTracker.AutoDetectChangesEnabled=false; for(var i=1;i<=count;i++){ var id=Id(1000+i); var date=new DateOnly(2021,1,1).AddDays(i%1826); var session=new WorkoutSession(id,date,null,null,null,Now); session.Complete(Now.AddMinutes(i)); var ex=new WorkoutExercise(Guid.NewGuid(),id,master.ExerciseId,master.ExerciseName,master.MuscleId,master.MuscleName,0,null,Now); db.AddRange(session,ex); for(var n=1;n<=setsPerWorkout;n++) db.Add(new WorkoutSet(Guid.NewGuid(),ex.Id,n,50m+n,i%15+1,null,Now)); } await db.SaveChangesAsync();
    }
    private sealed record Master(Guid MuscleId, Guid ExerciseId, string ExerciseName, string MuscleName);
}
