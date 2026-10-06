namespace WorkoutTracker.Application;

public enum TransferAggregateKind
{
    MuscleGroup,
    Exercise,
    WorkoutMenu,
    WorkoutSession
}

public sealed record TransferCounts(
    int MuscleGroups,
    int Exercises,
    int SecondaryMuscleLinks,
    int WorkoutMenus,
    int MenuExercises,
    int WorkoutSessions,
    int WorkoutExercises,
    int WorkoutSets);

public sealed record TransferManifestV1(
    string Format,
    int FormatVersion,
    Guid PackageId,
    DateTimeOffset CreatedUtc,
    string SourcePlatform,
    string AppVersion,
    int DatabaseSchemaVersion,
    string ExportType,
    string PayloadSha256,
    TransferCounts Counts);

public sealed record MuscleGroupTransferDto(
    Guid Id,
    string Name,
    int DisplayOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record ExerciseTransferDto(
    Guid Id,
    string Name,
    Guid PrimaryMuscleId,
    string? Note,
    bool IsActive,
    IReadOnlyList<Guid> SecondaryMuscleIds,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record MenuExerciseTransferDto(
    Guid Id,
    Guid WorkoutMenuId,
    Guid ExerciseId,
    int DisplayOrder,
    int DefaultSetCount,
    string? Note,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkoutMenuTransferDto(
    Guid Id,
    string Name,
    string? Description,
    IReadOnlyList<MenuExerciseTransferDto> Exercises,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkoutSetTransferDto(
    Guid Id,
    int SetNumber,
    decimal? WeightKg,
    int? Reps,
    decimal? Rpe,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkoutExerciseTransferDto(
    Guid Id,
    Guid WorkoutSessionId,
    Guid ExerciseId,
    string ExerciseNameSnapshot,
    Guid PrimaryMuscleIdSnapshot,
    string PrimaryMuscleNameSnapshot,
    int DisplayOrder,
    string? Note,
    IReadOnlyList<WorkoutSetTransferDto> Sets,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record WorkoutSessionTransferDto(
    Guid Id,
    Guid? SourceMenuId,
    string? Name,
    DateOnly WorkoutDate,
    string Status,
    string? Note,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<WorkoutExerciseTransferDto> Exercises,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record TransferDataV1(
    IReadOnlyList<MuscleGroupTransferDto> MuscleGroups,
    IReadOnlyList<ExerciseTransferDto> Exercises,
    IReadOnlyList<WorkoutMenuTransferDto> WorkoutMenus,
    IReadOnlyList<WorkoutSessionTransferDto> WorkoutSessions);

public sealed record TransferPackage(
    TransferManifestV1 Manifest,
    TransferDataV1 Data);

public sealed record TransferAggregateReference(TransferAggregateKind Kind, Guid Id, string Label);

public sealed record TransferConflict(
    TransferAggregateKind Kind,
    Guid Id,
    string Label,
    string Reason);

public sealed record ImportPlan(
    TransferPackage Package,
    IReadOnlyList<TransferAggregateReference> NewItems,
    IReadOnlyList<TransferAggregateReference> UnchangedItems,
    IReadOnlyList<TransferConflict> Conflicts,
    IReadOnlyList<string> Warnings)
{
    public bool CanCommit => Conflicts.Count == 0;
}

public sealed record ImportResolution(TransferAggregateKind Kind, Guid Id, bool UseImport);

public sealed record TransferFileResult(
    string Path,
    TransferManifestV1 Manifest);

public sealed record ImportResult(
    int Added,
    int Updated,
    int Unchanged,
    int ConflictsResolved);

public interface ITransferService
{
    Task<TransferFileResult> ExportAsync(string path, string sourcePlatform, string appVersion, CancellationToken cancellationToken = default);
    Task<ImportPlan> CreateImportPlanAsync(string path, CancellationToken cancellationToken = default);
    Task<ImportResult> ImportAsync(ImportPlan plan, IReadOnlyList<ImportResolution> resolutions, CancellationToken cancellationToken = default);
}
