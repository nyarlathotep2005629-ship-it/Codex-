namespace WorkoutTracker.Application;

public enum ScheduleSource { None, BaseWeek, SelectedWeek, SelectedDate }

public sealed record PlannedDayDto(DateOnly Date, Guid? MenuId, string? MenuName, int PlannedSetCount, bool IsRest, ScheduleSource Source);
public sealed record BaseDayDto(DayOfWeek Day, Guid? MenuId, bool IsRest);

public interface IWeeklyScheduleService
{
    Task<IReadOnlyList<BaseDayDto>> GetBaseWeekAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PlannedDayDto>> GetWeekAsync(DateOnly dateInWeek, CancellationToken cancellationToken = default);
    Task SaveBaseWeekAsync(IReadOnlyList<BaseDayDto> days, CancellationToken cancellationToken = default);
    Task SetBaseDayAsync(DayOfWeek day, Guid? menuId, bool isRest, CancellationToken cancellationToken = default);
    Task ClearBaseDayAsync(DayOfWeek day, CancellationToken cancellationToken = default);
    Task SetWeekDayAsync(DateOnly date, Guid? menuId, bool isRest, CancellationToken cancellationToken = default);
    Task SetDateAsync(DateOnly date, Guid? menuId, bool isRest, CancellationToken cancellationToken = default);
    Task ClearWeekDayAsync(DateOnly date, CancellationToken cancellationToken = default);
    Task ClearDateAsync(DateOnly date, CancellationToken cancellationToken = default);
}
