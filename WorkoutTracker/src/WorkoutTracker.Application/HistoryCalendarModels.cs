namespace WorkoutTracker.Application;

public sealed record HistoryFilterOption(Guid Id, string Name);
public sealed record HistoryFilterOptions(
    IReadOnlyList<HistoryFilterOption> Exercises,
    IReadOnlyList<HistoryFilterOption> PrimaryMuscles,
    IReadOnlyList<HistoryFilterOption> Menus);

public sealed record WorkoutDetailSetDto(Guid WorkoutSetId, int SetNumber, decimal? WeightKg, int? Reps, decimal? Rpe);
public sealed record WorkoutDetailExerciseDto(
    Guid WorkoutExerciseId,
    Guid ExerciseId,
    string ExerciseNameSnapshot,
    Guid PrimaryMuscleIdSnapshot,
    string PrimaryMuscleNameSnapshot,
    int DisplayOrder,
    string? Note,
    IReadOnlyList<WorkoutDetailSetDto> Sets)
{
    public decimal Volume => Sets.Where(x => x.WeightKg.HasValue && x.Reps.HasValue).Sum(x => x.WeightKg!.Value * x.Reps!.Value);
}
public sealed record WorkoutDetailDto(
    Guid WorkoutSessionId,
    DateOnly WorkoutDate,
    DateTimeOffset CompletedAtUtc,
    string? Name,
    string? SourceMenuName,
    string? Note,
    IReadOnlyList<WorkoutDetailExerciseDto> Exercises)
{
    public int TotalSets => Exercises.Sum(x => x.Sets.Count(s => s.WeightKg.HasValue && s.Reps.HasValue));
    public decimal TotalVolume => Exercises.Sum(x => x.Volume);
}

public sealed record CalendarWorkoutSummaryDto(
    Guid WorkoutSessionId,
    string? Name,
    string? SourceMenuName,
    IReadOnlyList<string> PrimaryMuscles,
    decimal TotalVolume,
    int TotalSets);
public sealed record CalendarDayDto(DateOnly Date, IReadOnlyList<CalendarWorkoutSummaryDto> Workouts)
{
    public bool HasWorkouts => Workouts.Count > 0;
    public int WorkoutCount => Workouts.Count;
}
public sealed record CalendarMonthDto(int Year, int Month, IReadOnlyList<CalendarDayDto> Days);

public interface IHistoryCalendarQuery
{
    Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken cancellationToken = default);
    Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken cancellationToken = default);
}
