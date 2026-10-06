namespace WorkoutTracker.Application;

public enum ActiveWorkoutLifecycle { NoDraft, Starting, ActiveDraft, Rehydrating, Finishing, Discarding, Error }
public enum WorkoutSaveState { Saved, Dirty, Saving, Failed }

public sealed record PreviousSetDto(int SetNumber, decimal WeightKg, int Reps, decimal? Rpe);
public sealed record PreviousPerformanceDto(Guid ExerciseId, DateOnly WorkoutDate, IReadOnlyList<PreviousSetDto> Sets);
public sealed record ActiveWorkoutSetDto(Guid WorkoutSetId, int SetNumber, decimal? WeightKg, int? Reps, decimal? Rpe, decimal? SuggestedWeightKg, int? SuggestedReps);
public sealed record ActiveWorkoutExerciseDto(Guid WorkoutExerciseId, Guid ExerciseId, string ExerciseName, Guid PrimaryMuscleId, string PrimaryMuscleName, int DisplayOrder, string? Note, PreviousPerformanceDto? PreviousPerformance, IReadOnlyList<ActiveWorkoutSetDto> Sets);
public sealed record ActiveWorkoutDto(Guid SessionId, Guid? SourceMenuId, string? WorkoutName, DateOnly WorkoutDate, string? WorkoutNote, IReadOnlyList<ActiveWorkoutExerciseDto> Exercises);

public sealed record SaveDraftSetRequest(Guid WorkoutSetId, int SetNumber, decimal? WeightKg, int? Reps, decimal? Rpe);
public sealed record SaveDraftExerciseRequest(Guid WorkoutExerciseId, Guid ExerciseId, string ExerciseNameSnapshot, Guid PrimaryMuscleIdSnapshot, string PrimaryMuscleNameSnapshot, int DisplayOrder, string? Note, IReadOnlyList<SaveDraftSetRequest> Sets);
public sealed record SaveDraftRequest(Guid SessionId, DateOnly WorkoutDate, string? WorkoutName, string? Note, IReadOnlyList<SaveDraftExerciseRequest> Exercises);
public sealed record StartWorkoutRequest(Guid? MenuId, DateOnly WorkoutDate);
public sealed record DraftSummaryDto(Guid SessionId, DateOnly WorkoutDate, string? WorkoutName);

public abstract record StartWorkoutResult
{
    private StartWorkoutResult() { }
    public sealed record Started(ActiveWorkoutDto Workout) : StartWorkoutResult;
    public sealed record ExistingDraft(DraftSummaryDto Draft) : StartWorkoutResult;
}

public sealed record FinishWorkoutResult(bool Succeeded, IReadOnlyList<string> Errors)
{
    public static FinishWorkoutResult Success() => new(true, []);
    public static FinishWorkoutResult Failure(params string[] errors) => new(false, errors);
}

public sealed record StartPersistenceResult(bool IsExistingDraft, ActiveWorkoutDto? Workout, DraftSummaryDto? ExistingDraft);

public interface IWorkoutVerticalSlicePersistence
{
    Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default);
    Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default);
    Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public interface IAutoSaveDelay
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}
