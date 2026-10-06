using WorkoutTracker.Domain;

namespace WorkoutTracker.Application;

public sealed class AnalyticsService(IAnalyticsQuery query, IOffUiExecutor executor, IWriteAdmissionGate? writeGate = null)
{
    public Task<IReadOnlyList<ExerciseOptionDto>> GetExerciseOptionsAsync(CancellationToken ct = default) =>
        executor.RunAsync(query.GetExerciseOptionsAsync, ct);

    public Task<AnalyticsSettings> GetSettingsAsync(CancellationToken ct = default) =>
        executor.RunAsync(query.GetSettingsAsync, ct);

    public Task SaveDashboardSettingsAsync(DashboardPeriod period, IReadOnlyList<Guid> exerciseIds, CancellationToken ct = default)
    {
        if (exerciseIds.Distinct().Count() > 4) throw new ArgumentOutOfRangeException(nameof(exerciseIds), "Dashboard supports at most four exercises.");
        return executor.RunAsync(async token => { using var lease = writeGate?.EnterPublicWrite(); await query.SaveDashboardSettingsAsync(new(period, exerciseIds.Distinct().Take(4).ToList()), token); }, ct);
    }

    public Task<DashboardDto> GetDashboardAsync(DashboardPeriod period, DateOnly displayDate, IReadOnlyList<Guid> exerciseIds, CancellationToken ct = default) =>
        executor.RunAsync(async token =>
        {
            if (exerciseIds.Distinct().Count() > 4) throw new ArgumentOutOfRangeException(nameof(exerciseIds));
            var oldest = period == DashboardPeriod.All ? await query.GetOldestCompletedWorkoutDateAsync(token) : null;
            var range = AnalyticsPeriodCalculator.Resolve(period, displayDate, oldest);
            return await query.GetDashboardAsync(new(range, period, exerciseIds.Distinct().Take(4).ToList()), token);
        }, ct);

    public Task<StatisticsDto> GetStatisticsAsync(Guid exerciseId, DashboardPeriod period, StatisticsMetric metric, DateOnly displayDate, CancellationToken ct = default) =>
        executor.RunAsync(async token =>
        {
            var oldest = period == DashboardPeriod.All ? await query.GetOldestCompletedWorkoutDateAsync(token) : null;
            var range = AnalyticsPeriodCalculator.Resolve(period, displayDate, oldest);
            return await query.GetStatisticsAsync(new(range, exerciseId, metric), token);
        }, ct);
}
