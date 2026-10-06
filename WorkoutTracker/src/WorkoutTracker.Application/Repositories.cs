using WorkoutTracker.Domain;

namespace WorkoutTracker.Application;

public sealed record HistoryCursor(DateOnly WorkoutDate, DateTimeOffset CompletedAtUtc, Guid Id);
public sealed record HistoryQuery(DateOnly? From, DateOnly? To, Guid? ExerciseId, Guid? PrimaryMuscleId, Guid? SourceMenuId, int PageSize, HistoryCursor? Cursor);
public sealed record WorkoutHistoryItem(Guid Id, DateOnly WorkoutDate, DateTimeOffset CompletedAtUtc, string? Name, Guid? SourceMenuId, string? SourceMenuName = null, decimal TotalVolume = 0m, int TotalSets = 0, int ExerciseCount = 0);
public sealed record HistoryPage(IReadOnlyList<WorkoutHistoryItem> Items, HistoryCursor? NextCursor) { public bool HasNext => NextCursor is not null; }
public sealed record ExerciseSummary(Guid Id, string Name, Guid PrimaryMuscleId, string PrimaryMuscleName);
public sealed record WorkoutMenuSummary(Guid Id, string Name, int ExerciseCount);

public interface IWorkoutSessionRepository
{
    Task<WorkoutSession?> GetDraftAsync(CancellationToken cancellationToken = default);
    Task<WorkoutSession?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task AddAsync(WorkoutSession session, CancellationToken cancellationToken = default);
    Task DeleteDraftAsync(Guid id, CancellationToken cancellationToken = default);
    Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkoutSession>> GetCalendarMonthAsync(int year, int month, CancellationToken cancellationToken = default);
}

public interface IExerciseRepository
{
    Task<IReadOnlyList<ExerciseSummary>> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<Exercise?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExerciseSummary>> SearchAsync(string text, CancellationToken cancellationToken = default);
}

public interface IWorkoutMenuRepository
{
    Task<IReadOnlyList<WorkoutMenuSummary>> GetSummariesAsync(CancellationToken cancellationToken = default);
    Task<WorkoutMenu?> GetWithExercisesAsync(Guid id, CancellationToken cancellationToken = default);
}
