using WorkoutTracker.Domain;

namespace WorkoutTracker.Application;

public enum StatisticsMetric { EstimatedOneRepMax, MaxWeight, Volume, Sets, Reps }
public enum ComparisonStatus { Increased, Decreased, Unchanged, NoBaseline, NotApplicable }

public sealed record AnalyticsPeriod(DateOnly Start, DateOnly EndExclusive, DateOnly? PreviousStart)
{
    public int DayCount => EndExclusive.DayNumber - Start.DayNumber;
    public DateOnly? PreviousEndExclusive => PreviousStart.HasValue ? Start : null;
}

public sealed record ExerciseOptionDto(Guid Id, string Name, bool IsSelected = false);
public sealed record ChartPointDto(DateOnly Date, decimal Value, string? Label = null, Guid? SourceId = null, decimal? WeightKg = null, int? Reps = null, string? Tooltip = null);
public sealed record ChartSeriesDto(Guid ExerciseId, string ExerciseName, string Unit, IReadOnlyList<ChartPointDto> Points);
public sealed record DashboardSummaryDto(DashboardPeriod Period, int WorkoutCount, decimal TotalVolume, int TotalSets, IReadOnlyList<SelectedExerciseSummaryDto> SelectedExerciseSummaries);
public sealed record SelectedExerciseSummaryDto(Guid ExerciseId, string ExerciseName, decimal? CurrentOneRepMax, decimal? BestOneRepMax, decimal? MaxWeight, int WorkoutCount);
public sealed record DashboardComparisonDto(string Metric, decimal CurrentValue, decimal PreviousValue, decimal AbsoluteDifference, decimal? PercentageDifference, ComparisonStatus Status);
public sealed record WorkoutFrequencyPointDto(DateOnly BucketStart, DateOnly BucketEndExclusive, int WorkoutCount);
public sealed record VolumeSeriesDto(DateOnly BucketStart, DateOnly BucketEndExclusive, decimal Volume);
public sealed record MuscleVolumeDto(Guid MuscleGroupId, string Name, decimal Volume, decimal Ratio);
public sealed record RecentWorkoutDto(Guid WorkoutSessionId, DateOnly Date, string Name, decimal Volume, int Sets, int Exercises);
public sealed record DashboardDto(
    AnalyticsPeriod Range,
    DashboardSummaryDto Summary,
    IReadOnlyList<DashboardComparisonDto> Comparisons,
    IReadOnlyList<WorkoutFrequencyPointDto> Frequency,
    IReadOnlyList<VolumeSeriesDto> VolumeByWeek,
    IReadOnlyList<MuscleVolumeDto> MuscleVolumes,
    IReadOnlyList<RecentWorkoutDto> RecentWorkouts,
    IReadOnlyList<ChartSeriesDto> OneRepMaxSeries);

public sealed record StatisticsDto(
    AnalyticsPeriod Range,
    Guid ExerciseId,
    string ExerciseName,
    StatisticsMetric Metric,
    decimal? CurrentOneRepMax,
    decimal? BestOneRepMax,
    Guid? BestOneRepMaxSourceId,
    decimal? MaxWeight,
    int WorkoutCount,
    ChartSeriesDto Series);

public sealed record AnalyticsSettings(DashboardPeriod Period, IReadOnlyList<Guid> DashboardExerciseIds);
public sealed record DashboardReadRequest(AnalyticsPeriod Period, DashboardPeriod SelectedPeriod, IReadOnlyList<Guid> ExerciseIds);
public sealed record StatisticsReadRequest(AnalyticsPeriod Period, Guid ExerciseId, StatisticsMetric Metric);

public interface IAnalyticsQuery
{
    Task<DateOnly?> GetOldestCompletedWorkoutDateAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExerciseOptionDto>> GetExerciseOptionsAsync(CancellationToken cancellationToken = default);
    Task<AnalyticsSettings> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveDashboardSettingsAsync(AnalyticsSettings settings, CancellationToken cancellationToken = default);
    Task<DashboardDto> GetDashboardAsync(DashboardReadRequest request, CancellationToken cancellationToken = default);
    Task<StatisticsDto> GetStatisticsAsync(StatisticsReadRequest request, CancellationToken cancellationToken = default);
}

public static class AnalyticsPeriodCalculator
{
    public static AnalyticsPeriod Resolve(DashboardPeriod period, DateOnly displayDate, DateOnly? oldestCompletedDate = null)
    {
        var end = displayDate.AddDays(1);
        if (period == DashboardPeriod.All)
        {
            var allStart = oldestCompletedDate ?? displayDate;
            return new(allStart, end, null);
        }

        var start = period switch
        {
            DashboardPeriod.OneMonth => displayDate.AddMonths(-1),
            DashboardPeriod.ThreeMonths => displayDate.AddMonths(-3),
            DashboardPeriod.SixMonths => displayDate.AddMonths(-6),
            DashboardPeriod.OneYear => displayDate.AddYears(-1),
            _ => throw new ArgumentOutOfRangeException(nameof(period))
        };
        var days = end.DayNumber - start.DayNumber;
        return new(start, end, start.AddDays(-days));
    }

    public static DateOnly StartOfWeekMonday(DateOnly date)
    {
        var offset = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-offset);
    }
}

public static class AnalyticsMath
{
    public static decimal EstimatedOneRepMax(int weightHundredthsKg, int reps)
    {
        if (weightHundredthsKg <= 0 || reps is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(reps));
        return weightHundredthsKg * (30m + reps) / 3000m;
    }

    public static DashboardComparisonDto Compare(string metric, decimal current, decimal previous, bool isAll)
    {
        var difference = current - previous;
        if (isAll) return new(metric, current, previous, difference, null, ComparisonStatus.NotApplicable);
        if (previous == 0m) return new(metric, current, previous, difference, null, ComparisonStatus.NoBaseline);
        return new(metric, current, previous, difference, difference / previous * 100m,
            difference > 0m ? ComparisonStatus.Increased : difference < 0m ? ComparisonStatus.Decreased : ComparisonStatus.Unchanged);
    }
}
