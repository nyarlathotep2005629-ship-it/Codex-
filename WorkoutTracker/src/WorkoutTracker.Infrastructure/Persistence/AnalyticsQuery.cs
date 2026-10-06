using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

public sealed class AnalyticsQuery(IDbContextFactory<WorkoutTrackerDbContext> factory, IClock clock) : IAnalyticsQuery
{
    public async Task<DateOnly?> GetOldestCompletedWorkoutDateAsync(CancellationToken ct = default) =>
        await WithConnectionAsync<DateOnly?>(async connection =>
        {
            await using var command = CreateCommand(connection, "SELECT MIN(WorkoutDate) FROM WorkoutSessions WHERE Status = 'Completed'");
            var value = await command.ExecuteScalarAsync(ct);
            return value is null or DBNull ? null : DateOnly.FromDayNumber(Convert.ToInt32(value, CultureInfo.InvariantCulture));
        }, ct);

    public async Task<IReadOnlyList<ExerciseOptionDto>> GetExerciseOptionsAsync(CancellationToken ct = default) =>
        await WithConnectionAsync<IReadOnlyList<ExerciseOptionDto>>(async connection =>
        {
            const string sql = """
                SELECT e.Id, e.Name
                FROM Exercises e
                WHERE e.IsActive = 1 OR EXISTS (
                    SELECT 1 FROM WorkoutExercises we
                    JOIN WorkoutSessions s ON s.Id = we.WorkoutSessionId
                    WHERE we.ExerciseId = e.Id AND s.Status = 'Completed')
                ORDER BY e.Name COLLATE NOCASE, e.Id
                """;
            return (IReadOnlyList<ExerciseOptionDto>)await ReadAsync(connection, sql, [], reader =>
                new ExerciseOptionDto(ReadGuid(reader, 0), reader.GetString(1)), ct);
        }, ct);

    public async Task<AnalyticsSettings> GetSettingsAsync(CancellationToken ct = default) =>
        await WithConnectionAsync<AnalyticsSettings>(async connection =>
        {
            await using var command = CreateCommand(connection, "SELECT DashboardPeriod, DashboardExerciseIdsJson FROM AppSettings WHERE Id = 1");
            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return new(DashboardPeriod.ThreeMonths, []);
            var period = Enum.TryParse<DashboardPeriod>(reader.GetString(0), out var parsed) ? parsed : DashboardPeriod.ThreeMonths;
            IReadOnlyList<Guid> ids;
            try { ids = JsonSerializer.Deserialize<List<Guid>>(reader.GetString(1))?.Distinct().Take(4).ToList() ?? []; }
            catch (JsonException) { ids = []; }
            return new(period, ids);
        }, ct);

    public async Task SaveDashboardSettingsAsync(AnalyticsSettings settings, CancellationToken ct = default)
    {
        if (settings.DashboardExerciseIds.Distinct().Count() > 4) throw new ArgumentOutOfRangeException(nameof(settings));
        await WithConnectionAsync<int>(async connection =>
        {
            const string sql = """
                INSERT INTO AppSettings
                    (Id, ThemeMode, DashboardPeriod, DashboardExerciseIdsJson, WindowLeft, WindowTop, WindowWidth, WindowHeight, WindowState, UpdatedAtUtcTicks)
                VALUES (1, 'System', $period, $ids, NULL, NULL, NULL, NULL, 'Normal', $updated)
                ON CONFLICT(Id) DO UPDATE SET
                    DashboardPeriod = excluded.DashboardPeriod,
                    DashboardExerciseIdsJson = excluded.DashboardExerciseIdsJson,
                    UpdatedAtUtcTicks = excluded.UpdatedAtUtcTicks
                """;
            await using var command = CreateCommand(connection, sql,
                ("$period", settings.Period.ToString()),
                ("$ids", JsonSerializer.Serialize(settings.DashboardExerciseIds.Distinct().Take(4))),
                ("$updated", clock.UtcNow.UtcTicks));
            await command.ExecuteNonQueryAsync(ct);
            return 0;
        }, ct);
    }

    public async Task<DashboardDto> GetDashboardAsync(DashboardReadRequest request, CancellationToken ct = default) =>
        await WithConnectionAsync<DashboardDto>(async connection =>
        {
            var current = await ReadTotalsAsync(connection, request.Period.Start, request.Period.EndExclusive, ct);
            var previous = request.Period.PreviousStart.HasValue
                ? await ReadTotalsAsync(connection, request.Period.PreviousStart.Value, request.Period.Start, ct)
                : Totals.Empty;
            var dailyWorkouts = await ReadDailyWorkoutCountsAsync(connection, request.Period.Start, request.Period.EndExclusive, ct);
            var dailyVolumes = await ReadDailyVolumesAsync(connection, request.Period.Start, request.Period.EndExclusive, null, ct);
            var frequency = BuildFrequency(request.Period, dailyWorkouts);
            var volumeByWeek = BuildVolumeWeeks(request.Period, dailyVolumes);
            var recent = await ReadRecentAsync(connection, request.Period.Start, request.Period.EndExclusive, ct);
            var muscles = await ReadMusclesAsync(connection, request.Period.Start, request.Period.EndExclusive, ct);
            var oneRepMaxSeries = await ReadOneRepMaxSeriesAsync(connection, request.Period.Start, request.Period.EndExclusive, request.ExerciseIds, ct);
            var exerciseSummaries = await ReadSelectedExerciseSummariesAsync(connection, request.Period.Start, request.Period.EndExclusive, request.ExerciseIds, oneRepMaxSeries, ct);
            var comparisons = new List<DashboardComparisonDto>
            {
                AnalyticsMath.Compare("Workout Count", current.WorkoutCount, previous.WorkoutCount, request.SelectedPeriod == DashboardPeriod.All),
                AnalyticsMath.Compare("Total Volume", current.Volume, previous.Volume, request.SelectedPeriod == DashboardPeriod.All),
                AnalyticsMath.Compare("Total Sets", current.SetCount, previous.SetCount, request.SelectedPeriod == DashboardPeriod.All)
            };
            return new(request.Period,
                new(request.SelectedPeriod, current.WorkoutCount, current.Volume, current.SetCount, exerciseSummaries),
                comparisons, frequency, volumeByWeek, muscles, recent, oneRepMaxSeries);
        }, ct);

    public async Task<StatisticsDto> GetStatisticsAsync(StatisticsReadRequest request, CancellationToken ct = default) =>
        await WithConnectionAsync<StatisticsDto>(async connection =>
        {
            var exerciseName = await ReadExerciseNameAsync(connection, request.ExerciseId, ct);
            var daily = await ReadStatisticsDailyAsync(connection, request.Period.Start, request.Period.EndExclusive, request.ExerciseId, ct);
            var oneRepPoints = daily.Where(x => x.OneRepNumerator.HasValue).Select(x => new ChartPointDto(x.Date, x.OneRepNumerator!.Value / 3000m, exerciseName, x.OneRepSourceId, x.OneRepWeightHundredths / 100m, x.OneRepReps)).ToList();
            var maxWeightRows = daily.Where(x => x.MaxWeightHundredths.HasValue).Select(x => new ChartPointDto(x.Date, x.MaxWeightHundredths!.Value / 100m, exerciseName, x.MaxWeightSourceId, x.MaxWeightHundredths / 100m, x.MaxWeightReps)).ToList();
            var workoutCount = await ReadExerciseWorkoutCountAsync(connection, request.Period.Start, request.Period.EndExclusive, request.ExerciseId, ct);
            IReadOnlyList<ChartPointDto> metricPoints = request.Metric switch
            {
                StatisticsMetric.EstimatedOneRepMax => oneRepPoints,
                StatisticsMetric.MaxWeight => maxWeightRows,
                StatisticsMetric.Volume => daily.Select(x => new ChartPointDto(x.Date, x.VolumeHundredths / 100m)).ToList(),
                StatisticsMetric.Sets => daily.Select(x => new ChartPointDto(x.Date, x.SetCount)).ToList(),
                StatisticsMetric.Reps => daily.Select(x => new ChartPointDto(x.Date, x.MaxReps, exerciseName, x.MaxRepSourceId, x.MaxRepWeightHundredths / 100m, x.MaxReps)).ToList(),
                _ => throw new ArgumentOutOfRangeException(nameof(request.Metric))
            };
            var unit = UnitFor(request.Metric);
            var labeled = metricPoints.Select(x => x with { Label = exerciseName, Tooltip = BuildTooltip(x, exerciseName, request.Metric, unit) }).ToList();
            var best = oneRepPoints.OrderByDescending(x => x.Value).ThenBy(x => x.Date).FirstOrDefault();
            return new(request.Period, request.ExerciseId, exerciseName, request.Metric,
                oneRepPoints.LastOrDefault()?.Value, best?.Value, best?.SourceId,
                maxWeightRows.Count == 0 ? null : maxWeightRows.Max(x => x.Value), workoutCount,
                new(request.ExerciseId, exerciseName, unit, labeled));
        }, ct);

    private static async Task<IReadOnlyList<StatisticsDailyRow>> ReadStatisticsDailyAsync(DbConnection connection, DateOnly start, DateOnly end, Guid exerciseId, CancellationToken ct)
    {
        const string sql = """
            SELECT s.WorkoutDate,
                MAX(CASE WHEN ws.Reps BETWEEN 1 AND 15 THEN printf('%010d|%020d|%s|%04d|%06d|%04d', ws.WeightHundredthsKg * (30 + ws.Reps), s.CompletedAtUtcTicks, s.Id, ws.SetNumber, ws.WeightHundredthsKg, ws.Reps) END),
                MAX(printf('%06d|%020d|%s|%04d|%04d', ws.WeightHundredthsKg, s.CompletedAtUtcTicks, s.Id, ws.SetNumber, ws.Reps)),
                SUM(ws.WeightHundredthsKg * ws.Reps), COUNT(*),
                MAX(printf('%04d|%020d|%s|%04d|%06d', ws.Reps, s.CompletedAtUtcTicks, s.Id, ws.SetNumber, ws.WeightHundredthsKg))
            FROM WorkoutSessions s
            JOIN WorkoutExercises we ON we.WorkoutSessionId = s.Id
            JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id
            WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end
              AND we.ExerciseId = $exercise AND ws.WeightHundredthsKg IS NOT NULL AND ws.Reps IS NOT NULL
            GROUP BY s.WorkoutDate ORDER BY s.WorkoutDate
            """;
        return await ReadAsync(connection, sql, [("$start", start.DayNumber), ("$end", end.DayNumber), ("$exercise", exerciseId)], reader =>
        {
            var oneRep = reader.IsDBNull(1) ? null : reader.GetString(1).Split('|'); var maxWeight = reader.GetString(2).Split('|'); var maxReps = reader.GetString(5).Split('|');
            return new StatisticsDailyRow(DateOnly.FromDayNumber(reader.GetInt32(0)), oneRep is null ? null : long.Parse(oneRep[0], CultureInfo.InvariantCulture), oneRep is null ? null : Guid.Parse(oneRep[2]), oneRep is null ? null : int.Parse(oneRep[4], CultureInfo.InvariantCulture), oneRep is null ? null : int.Parse(oneRep[5], CultureInfo.InvariantCulture),
                int.Parse(maxWeight[0], CultureInfo.InvariantCulture), Guid.Parse(maxWeight[2]), int.Parse(maxWeight[4], CultureInfo.InvariantCulture), reader.GetInt64(3), reader.GetInt32(4), int.Parse(maxReps[0], CultureInfo.InvariantCulture), Guid.Parse(maxReps[2]), int.Parse(maxReps[4], CultureInfo.InvariantCulture));
        }, ct);
    }

    private async Task<T> WithConnectionAsync<T>(Func<DbConnection, Task<T>> action, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(ct);
        return await action(connection);
    }

    private static async Task<Totals> ReadTotalsAsync(DbConnection connection, DateOnly start, DateOnly end, CancellationToken ct)
    {
        const string sql = """
            SELECT
                (SELECT COUNT(*) FROM WorkoutSessions s WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end),
                COALESCE(SUM(ws.WeightHundredthsKg * ws.Reps), 0),
                COUNT(ws.Id)
            FROM WorkoutSets ws
            JOIN WorkoutExercises we ON we.Id = ws.WorkoutExerciseId
            JOIN WorkoutSessions s ON s.Id = we.WorkoutSessionId
            WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end
              AND ws.WeightHundredthsKg IS NOT NULL AND ws.Reps IS NOT NULL
            """;
        await using var command = CreateCommand(connection, sql, ("$start", start.DayNumber), ("$end", end.DayNumber));
        await using var reader = await command.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        return new(reader.GetInt32(0), reader.GetInt64(1) / 100m, reader.GetInt32(2));
    }

    private static async Task<Dictionary<DateOnly, int>> ReadDailyWorkoutCountsAsync(DbConnection connection, DateOnly start, DateOnly end, CancellationToken ct)
    {
        const string sql = "SELECT WorkoutDate, COUNT(*) FROM WorkoutSessions INDEXED BY IX_WorkoutSessions_History_Keyset WHERE Status = 'Completed' AND WorkoutDate >= $start AND WorkoutDate < $end GROUP BY WorkoutDate ORDER BY WorkoutDate";
        var rows = await ReadAsync(connection, sql, [("$start", start.DayNumber), ("$end", end.DayNumber)], reader =>
            new KeyValuePair<DateOnly, int>(DateOnly.FromDayNumber(reader.GetInt32(0)), reader.GetInt32(1)), ct);
        return rows.ToDictionary(x => x.Key, x => x.Value);
    }

    private static async Task<Dictionary<DateOnly, decimal>> ReadDailyVolumesAsync(DbConnection connection, DateOnly start, DateOnly end, Guid? exerciseId, CancellationToken ct)
    {
        var filter = exerciseId.HasValue ? " AND we.ExerciseId = $exercise" : string.Empty;
        var parameters = new List<(string, object?)> { ("$start", start.DayNumber), ("$end", end.DayNumber) };
        if (exerciseId.HasValue) parameters.Add(("$exercise", exerciseId.Value));
        var sql = $"""
            SELECT s.WorkoutDate, COALESCE(SUM(ws.WeightHundredthsKg * ws.Reps), 0)
            FROM WorkoutSessions s
            JOIN WorkoutExercises we ON we.WorkoutSessionId = s.Id
            JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id
            WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end
              AND ws.WeightHundredthsKg IS NOT NULL AND ws.Reps IS NOT NULL{filter}
            GROUP BY s.WorkoutDate ORDER BY s.WorkoutDate
            """;
        var rows = await ReadAsync(connection, sql, parameters, reader => new KeyValuePair<DateOnly, decimal>(DateOnly.FromDayNumber(reader.GetInt32(0)), reader.GetInt64(1) / 100m), ct);
        return rows.ToDictionary(x => x.Key, x => x.Value);
    }

    private static IReadOnlyList<WorkoutFrequencyPointDto> BuildFrequency(AnalyticsPeriod period, IReadOnlyDictionary<DateOnly, int> daily)
    {
        var result = new List<WorkoutFrequencyPointDto>();
        for (var bucket = AnalyticsPeriodCalculator.StartOfWeekMonday(period.Start); bucket < period.EndExclusive; bucket = bucket.AddDays(7))
        {
            var from = bucket < period.Start ? period.Start : bucket;
            var to = bucket.AddDays(7) > period.EndExclusive ? period.EndExclusive : bucket.AddDays(7);
            var count = daily.Where(x => x.Key >= from && x.Key < to).Sum(x => x.Value);
            result.Add(new(bucket, bucket.AddDays(7), count));
        }
        return result;
    }

    private static IReadOnlyList<VolumeSeriesDto> BuildVolumeWeeks(AnalyticsPeriod period, IReadOnlyDictionary<DateOnly, decimal> daily)
    {
        var result = new List<VolumeSeriesDto>();
        for (var bucket = AnalyticsPeriodCalculator.StartOfWeekMonday(period.Start); bucket < period.EndExclusive; bucket = bucket.AddDays(7))
        {
            var from = bucket < period.Start ? period.Start : bucket;
            var to = bucket.AddDays(7) > period.EndExclusive ? period.EndExclusive : bucket.AddDays(7);
            result.Add(new(bucket, bucket.AddDays(7), daily.Where(x => x.Key >= from && x.Key < to).Sum(x => x.Value)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<RecentWorkoutDto>> ReadRecentAsync(DbConnection connection, DateOnly start, DateOnly end, CancellationToken ct)
    {
        const string sql = """
            SELECT s.Id, s.WorkoutDate, COALESCE(NULLIF(s.Name, ''), 'Workout'),
                COALESCE((SELECT SUM(ws.WeightHundredthsKg * ws.Reps) FROM WorkoutExercises we JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id WHERE we.WorkoutSessionId = s.Id AND ws.WeightHundredthsKg IS NOT NULL AND ws.Reps IS NOT NULL), 0),
                COALESCE((SELECT COUNT(*) FROM WorkoutExercises we JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id WHERE we.WorkoutSessionId = s.Id AND ws.WeightHundredthsKg IS NOT NULL AND ws.Reps IS NOT NULL), 0),
                COALESCE((SELECT COUNT(*) FROM WorkoutExercises we WHERE we.WorkoutSessionId = s.Id), 0)
            FROM WorkoutSessions s INDEXED BY IX_WorkoutSessions_History_Keyset
            WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end
            ORDER BY s.WorkoutDate DESC, s.CompletedAtUtcTicks DESC, s.Id DESC LIMIT 5
            """;
        return await ReadAsync(connection, sql, [("$start", start.DayNumber), ("$end", end.DayNumber)], reader =>
            new RecentWorkoutDto(ReadGuid(reader, 0), DateOnly.FromDayNumber(reader.GetInt32(1)), reader.GetString(2), reader.GetInt64(3) / 100m, reader.GetInt32(4), reader.GetInt32(5)), ct);
    }

    private static async Task<IReadOnlyList<MuscleVolumeDto>> ReadMusclesAsync(DbConnection connection, DateOnly start, DateOnly end, CancellationToken ct)
    {
        const string sql = """
            SELECT we.PrimaryMuscleIdSnapshot, we.PrimaryMuscleNameSnapshot, SUM(ws.WeightHundredthsKg * ws.Reps)
            FROM WorkoutSessions s
            JOIN WorkoutExercises we ON we.WorkoutSessionId = s.Id
            JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id
            WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end
              AND ws.WeightHundredthsKg IS NOT NULL AND ws.Reps IS NOT NULL
            GROUP BY we.PrimaryMuscleIdSnapshot, we.PrimaryMuscleNameSnapshot
            ORDER BY SUM(ws.WeightHundredthsKg * ws.Reps) DESC, we.PrimaryMuscleNameSnapshot
            """;
        var rows = await ReadAsync(connection, sql, [("$start", start.DayNumber), ("$end", end.DayNumber)], reader =>
            (Id: ReadGuid(reader, 0), Name: reader.GetString(1), Volume: reader.GetInt64(2) / 100m), ct);
        var total = rows.Sum(x => x.Volume);
        return rows.Select(x => new MuscleVolumeDto(x.Id, x.Name, x.Volume, total == 0m ? 0m : x.Volume / total)).ToList();
    }

    private static async Task<IReadOnlyList<ChartSeriesDto>> ReadOneRepMaxSeriesAsync(DbConnection connection, DateOnly start, DateOnly end, IReadOnlyList<Guid> exerciseIds, CancellationToken ct)
    {
        if (exerciseIds.Count == 0) return [];
        var parameters = new List<(string, object?)> { ("$start", start.DayNumber), ("$end", end.DayNumber) };
        var placeholders = new List<string>();
        for (var i = 0; i < exerciseIds.Count; i++) { var key = $"$exercise{i}"; placeholders.Add(key); parameters.Add((key, exerciseIds[i])); }
        var sql = $"""
            WITH Eligible AS MATERIALIZED (
                SELECT we.ExerciseId, we.ExerciseNameSnapshot, s.WorkoutDate, s.Id,
                    ws.WeightHundredthsKg, ws.Reps,
                    ws.WeightHundredthsKg * (30 + ws.Reps) AS Numerator,
                    s.CompletedAtUtcTicks, ws.SetNumber
                FROM WorkoutSessions s
                JOIN WorkoutExercises we ON we.WorkoutSessionId = s.Id
                JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id
                WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end
                  AND we.ExerciseId IN ({string.Join(",", placeholders)})
                  AND ws.WeightHundredthsKg > 0 AND ws.Reps BETWEEN 1 AND 15),
            Daily AS (
                SELECT ExerciseId, WorkoutDate, MAX(Numerator) AS Numerator
                FROM Eligible GROUP BY ExerciseId, WorkoutDate)
            SELECT e.ExerciseId, e.ExerciseNameSnapshot, e.WorkoutDate, e.Id, e.WeightHundredthsKg, e.Reps, e.Numerator
            FROM Eligible e
            JOIN Daily d ON d.ExerciseId = e.ExerciseId AND d.WorkoutDate = e.WorkoutDate AND d.Numerator = e.Numerator
            ORDER BY e.ExerciseId, e.WorkoutDate, e.CompletedAtUtcTicks DESC, e.Id DESC, e.SetNumber
            """;
        var rows = await ReadAsync(connection, sql, parameters, reader => new OneRepRow(ReadGuid(reader, 0), reader.GetString(1), DateOnly.FromDayNumber(reader.GetInt32(2)), ReadGuid(reader, 3), reader.GetInt32(4), reader.GetInt32(5), reader.GetInt64(6)), ct);
        return exerciseIds.Select(id =>
        {
            var values = rows.Where(x => x.ExerciseId == id).GroupBy(x => x.Date).Select(x => x.First()).OrderBy(x => x.Date).ToList();
            var name = values.LastOrDefault()?.ExerciseName ?? id.ToString();
            return new ChartSeriesDto(id, name, "kg", values.Select(x => new ChartPointDto(x.Date, x.Numerator / 3000m, name, x.SourceId, x.WeightHundredths / 100m, x.Reps,
                $"{x.Date:yyyy/MM/dd}  {name}  1RM {x.Numerator / 3000m:0.0} kg  ({x.WeightHundredths / 100m:0.##} kg × {x.Reps})")).ToList());
        }).ToList();
    }

    private static async Task<IReadOnlyList<SelectedExerciseSummaryDto>> ReadSelectedExerciseSummariesAsync(DbConnection connection, DateOnly start, DateOnly end, IReadOnlyList<Guid> ids, IReadOnlyList<ChartSeriesDto> series, CancellationToken ct)
    {
        var result = new List<SelectedExerciseSummaryDto>();
        foreach (var id in ids)
        {
            var name = await ReadExerciseNameAsync(connection, id, ct);
            var maxWeight = await ReadRankedMetricAsync(connection, start, end, id, "ws.WeightHundredthsKg", "ws.WeightHundredthsKg IS NOT NULL AND ws.Reps IS NOT NULL", 100m, ct);
            var workoutCount = await ReadExerciseWorkoutCountAsync(connection, start, end, id, ct);
            var points = series.FirstOrDefault(x => x.ExerciseId == id)?.Points ?? [];
            result.Add(new(id, name, points.LastOrDefault()?.Value, points.Count == 0 ? null : points.Max(x => x.Value), maxWeight.Count == 0 ? null : maxWeight.Max(x => x.Value), workoutCount));
        }
        return result;
    }

    private static async Task<string> ReadExerciseNameAsync(DbConnection connection, Guid id, CancellationToken ct)
    {
        await using var command = CreateCommand(connection, "SELECT Name FROM Exercises WHERE Id = $id", ("$id", id));
        return Convert.ToString(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture) ?? id.ToString();
    }

    private static async Task<int> ReadExerciseWorkoutCountAsync(DbConnection connection, DateOnly start, DateOnly end, Guid id, CancellationToken ct)
    {
        const string sql = "SELECT COUNT(DISTINCT s.Id) FROM WorkoutSessions s JOIN WorkoutExercises we ON we.WorkoutSessionId = s.Id WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end AND we.ExerciseId = $exercise";
        await using var command = CreateCommand(connection, sql, ("$start", start.DayNumber), ("$end", end.DayNumber), ("$exercise", id));
        return Convert.ToInt32(await command.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<ChartPointDto>> ReadRankedMetricAsync(DbConnection connection, DateOnly start, DateOnly end, Guid id, string expression, string eligibility, decimal divisor, CancellationToken ct)
    {
        var sql = $"""
            WITH Eligible AS MATERIALIZED (
                SELECT s.WorkoutDate, s.Id, ws.WeightHundredthsKg, ws.Reps, {expression} AS MetricValue,
                    s.CompletedAtUtcTicks, ws.SetNumber
                FROM WorkoutSessions s
                JOIN WorkoutExercises we ON we.WorkoutSessionId = s.Id
                JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id
                WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end AND we.ExerciseId = $exercise AND {eligibility}),
            Daily AS (SELECT WorkoutDate, MAX(MetricValue) AS MetricValue FROM Eligible GROUP BY WorkoutDate)
            SELECT e.WorkoutDate, e.Id, e.WeightHundredthsKg, e.Reps, e.MetricValue
            FROM Eligible e JOIN Daily d ON d.WorkoutDate = e.WorkoutDate AND d.MetricValue = e.MetricValue
            ORDER BY e.WorkoutDate, e.CompletedAtUtcTicks DESC, e.Id DESC, e.SetNumber
            """;
        var rows = await ReadAsync(connection, sql, [("$start", start.DayNumber), ("$end", end.DayNumber), ("$exercise", id)], reader =>
            new ChartPointDto(DateOnly.FromDayNumber(reader.GetInt32(0)), Convert.ToDecimal(reader.GetValue(4), CultureInfo.InvariantCulture) / divisor, null, ReadGuid(reader, 1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2) / 100m, reader.IsDBNull(3) ? null : reader.GetInt32(3)), ct);
        return rows.GroupBy(x => x.Date).Select(x => x.First()).OrderBy(x => x.Date).ToList();
    }

    private static async Task<IReadOnlyList<ChartPointDto>> ReadAggregateMetricAsync(DbConnection connection, DateOnly start, DateOnly end, Guid id, string expression, string eligibility, decimal divisor, string unit, CancellationToken ct)
    {
        var sql = $"""
            SELECT s.WorkoutDate, {expression}
            FROM WorkoutSessions s
            JOIN WorkoutExercises we ON we.WorkoutSessionId = s.Id
            JOIN WorkoutSets ws ON ws.WorkoutExerciseId = we.Id
            WHERE s.Status = 'Completed' AND s.WorkoutDate >= $start AND s.WorkoutDate < $end AND we.ExerciseId = $exercise AND {eligibility}
            GROUP BY s.WorkoutDate ORDER BY s.WorkoutDate
            """;
        return await ReadAsync(connection, sql, [("$start", start.DayNumber), ("$end", end.DayNumber), ("$exercise", id)], reader =>
            new ChartPointDto(DateOnly.FromDayNumber(reader.GetInt32(0)), Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture) / divisor, null, null, null, null,
                $"{DateOnly.FromDayNumber(reader.GetInt32(0)):yyyy/MM/dd}  {Convert.ToDecimal(reader.GetValue(1), CultureInfo.InvariantCulture) / divisor:0.##} {unit}"), ct);
    }

    private static string UnitFor(StatisticsMetric metric) => metric switch
    {
        StatisticsMetric.EstimatedOneRepMax or StatisticsMetric.MaxWeight or StatisticsMetric.Volume => "kg",
        StatisticsMetric.Sets => "sets",
        StatisticsMetric.Reps => "reps",
        _ => string.Empty
    };

    private static string BuildTooltip(ChartPointDto point, string exercise, StatisticsMetric metric, string unit)
    {
        var detail = point.WeightKg.HasValue && point.Reps.HasValue ? $"  ({point.WeightKg:0.##} kg × {point.Reps})" : string.Empty;
        return $"{point.Date:yyyy/MM/dd}  {exercise}  {metric}: {point.Value:0.##} {unit}{detail}";
    }

    private static DbCommand CreateCommand(DbConnection connection, string sql, params (string Name, object? Value)[] parameters) => CreateCommand(connection, sql, (IEnumerable<(string, object?)>)parameters);
    private static DbCommand CreateCommand(DbConnection connection, string sql, IEnumerable<(string Name, object? Value)> parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var item in parameters) { var parameter = command.CreateParameter(); parameter.ParameterName = item.Name; parameter.Value = item.Value ?? DBNull.Value; command.Parameters.Add(parameter); }
        return command;
    }

    private static async Task<List<T>> ReadAsync<T>(DbConnection connection, string sql, IEnumerable<(string Name, object? Value)> parameters, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var command = CreateCommand(connection, sql, parameters);
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<T>(); while (await reader.ReadAsync(ct)) result.Add(map(reader)); return result;
    }

    private static Guid ReadGuid(DbDataReader reader, int ordinal) => reader.GetFieldValue<Guid>(ordinal);
    private static Guid? ReadNullableGuid(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : ReadGuid(reader, ordinal);
    private sealed record Totals(int WorkoutCount, decimal Volume, int SetCount) { public static Totals Empty { get; } = new(0, 0m, 0); }
    private sealed record OneRepRow(Guid ExerciseId, string ExerciseName, DateOnly Date, Guid SourceId, int WeightHundredths, int Reps, long Numerator);
    private sealed record StatisticsDailyRow(DateOnly Date, long? OneRepNumerator, Guid? OneRepSourceId, int? OneRepWeightHundredths, int? OneRepReps, int? MaxWeightHundredths, Guid? MaxWeightSourceId, int? MaxWeightReps, long VolumeHundredths, int SetCount, int MaxReps, Guid? MaxRepSourceId, int? MaxRepWeightHundredths);
}
