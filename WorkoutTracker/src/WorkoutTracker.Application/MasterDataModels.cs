namespace WorkoutTracker.Application;

public sealed record MuscleGroupDto(Guid Id, string Name, int DisplayOrder, DateTimeOffset UpdatedAtUtc);
public sealed record ExerciseMasterDto(
    Guid Id,
    string Name,
    Guid PrimaryMuscleId,
    string PrimaryMuscleName,
    IReadOnlyList<Guid> SecondaryMuscleIds,
    IReadOnlyList<string> SecondaryMuscleNames,
    string? Note,
    bool IsActive,
    bool IsReferenced,
    DateTimeOffset UpdatedAtUtc);
public sealed record MenuExerciseDto(Guid Id, Guid ExerciseId, string ExerciseName, int DisplayOrder, int DefaultSetCount, string? Note);
public sealed record WorkoutMenuMasterDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<MenuExerciseDto> Exercises,
    DateTimeOffset UpdatedAtUtc)
{
    public int PlannedSetCount => Exercises.Sum(x => x.DefaultSetCount);
}

public sealed record SaveMuscleGroupRequest(Guid? Id, string Name, int DisplayOrder);
public sealed record SaveExerciseRequest(Guid? Id, string Name, Guid PrimaryMuscleId, IReadOnlyList<Guid> SecondaryMuscleIds, string? Note, bool IsActive = true);
public sealed record SaveMenuExerciseRequest(Guid? Id, Guid ExerciseId, int DefaultSetCount, string? Note);
public sealed record SaveWorkoutMenuRequest(Guid? Id, string Name, string? Description, IReadOnlyList<SaveMenuExerciseRequest> Exercises);

public enum ExerciseDeleteResult { Deleted, ReferencedMustHide, NotFound }
public enum MuscleGroupDeleteResult { Deleted, Referenced, NotFound }

public interface IMasterDataPersistence
{
    Task<IReadOnlyList<MuscleGroupDto>> GetMuscleGroupsAsync(CancellationToken cancellationToken = default);
    Task<MuscleGroupDto> SaveMuscleGroupAsync(SaveMuscleGroupRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task ReorderMuscleGroupsAsync(IReadOnlyList<Guid> orderedIds, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<MuscleGroupDeleteResult> DeleteMuscleGroupAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ExerciseMasterDto>> GetExercisesAsync(bool includeInactive, CancellationToken cancellationToken = default);
    Task<ExerciseMasterDto> SaveExerciseAsync(SaveExerciseRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task SetExerciseActiveAsync(Guid id, bool isActive, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<ExerciseDeleteResult> DeleteExerciseAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WorkoutMenuMasterDto>> GetMenusAsync(CancellationToken cancellationToken = default);
    Task<WorkoutMenuMasterDto> SaveMenuAsync(SaveWorkoutMenuRequest request, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<WorkoutMenuMasterDto> DuplicateMenuAsync(Guid id, string duplicateName, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task DeleteMenuAsync(Guid id, CancellationToken cancellationToken = default);
}
