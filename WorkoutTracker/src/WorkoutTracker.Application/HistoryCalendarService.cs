namespace WorkoutTracker.Application;

public sealed class HistoryCalendarService(IHistoryCalendarQuery query, IOffUiExecutor executor)
{
    public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery request, CancellationToken ct = default) => executor.RunAsync(token => query.GetHistoryPageAsync(request, token), ct);
    public Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken ct = default) => executor.RunAsync(token => query.GetHistoryFilterOptionsAsync(token), ct);
    public Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken ct = default) => executor.RunAsync(token => query.GetWorkoutDetailAsync(sessionId, token), ct);
    public Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken ct = default) => executor.RunAsync(token => query.GetCalendarMonthAsync(year, month, token), ct);
}
