namespace WorkoutTracker.Application;

public sealed class MasterDataService(IMasterDataPersistence persistence, IClock clock, IOffUiExecutor executor, IWriteAdmissionGate? writeGate = null)
{
    public Task<IReadOnlyList<MuscleGroupDto>> GetMuscleGroupsAsync(CancellationToken ct = default) => executor.RunAsync(token => persistence.GetMuscleGroupsAsync(token), ct);
    public Task<MuscleGroupDto> SaveMuscleGroupAsync(SaveMuscleGroupRequest request, CancellationToken ct = default) => WriteAsync(token => persistence.SaveMuscleGroupAsync(request, clock.UtcNow, token), ct);
    public Task ReorderMuscleGroupsAsync(IReadOnlyList<Guid> ids, CancellationToken ct = default) => WriteAsync(token => persistence.ReorderMuscleGroupsAsync(ids, clock.UtcNow, token), ct);
    public Task<MuscleGroupDeleteResult> DeleteMuscleGroupAsync(Guid id, CancellationToken ct = default) => WriteAsync(token => persistence.DeleteMuscleGroupAsync(id, token), ct);
    public Task<IReadOnlyList<ExerciseMasterDto>> GetExercisesAsync(bool includeInactive = true, CancellationToken ct = default) => executor.RunAsync(token => persistence.GetExercisesAsync(includeInactive, token), ct);
    public Task<ExerciseMasterDto> SaveExerciseAsync(SaveExerciseRequest request, CancellationToken ct = default) => WriteAsync(token => persistence.SaveExerciseAsync(request, clock.UtcNow, token), ct);
    public Task SetExerciseActiveAsync(Guid id, bool active, CancellationToken ct = default) => WriteAsync(token => persistence.SetExerciseActiveAsync(id, active, clock.UtcNow, token), ct);
    public Task<ExerciseDeleteResult> DeleteExerciseAsync(Guid id, CancellationToken ct = default) => WriteAsync(token => persistence.DeleteExerciseAsync(id, token), ct);
    public Task<IReadOnlyList<WorkoutMenuMasterDto>> GetMenusAsync(CancellationToken ct = default) => executor.RunAsync(token => persistence.GetMenusAsync(token), ct);
    public Task<WorkoutMenuMasterDto> SaveMenuAsync(SaveWorkoutMenuRequest request, CancellationToken ct = default) => WriteAsync(token => persistence.SaveMenuAsync(request, clock.UtcNow, token), ct);
    public Task<WorkoutMenuMasterDto> DuplicateMenuAsync(Guid id, string duplicateName, CancellationToken ct = default) => WriteAsync(token => persistence.DuplicateMenuAsync(id, duplicateName, clock.UtcNow, token), ct);
    public Task DeleteMenuAsync(Guid id, CancellationToken ct = default) => WriteAsync(token => persistence.DeleteMenuAsync(id, token), ct);

    private Task WriteAsync(Func<CancellationToken, Task> operation, CancellationToken ct) => executor.RunAsync(async token => { using var lease = writeGate?.EnterPublicWrite(); await operation(token); }, ct);
    private Task<T> WriteAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken ct) => executor.RunAsync(async token => { using var lease = writeGate?.EnterPublicWrite(); return await operation(token); }, ct);
}
