namespace WorkoutTracker.Application;

public sealed class StartWorkoutUseCase(IWorkoutVerticalSlicePersistence persistence, ActiveWorkoutState state, IClock clock, IOffUiExecutor executor, IWriteAdmissionGate? writeGate = null)
{
    public async Task<StartWorkoutResult> ExecuteAsync(StartWorkoutRequest request, CancellationToken cancellationToken = default)
    {
        using var lease = writeGate?.EnterPublicWrite();
        if (request.WorkoutDate < new DateOnly(1900, 1, 1) || request.WorkoutDate > DateOnly.FromDateTime(clock.UtcNow.ToLocalTime().DateTime)) throw new ArgumentOutOfRangeException(nameof(request.WorkoutDate));
        state.Begin(ActiveWorkoutLifecycle.Starting);
        var result = await executor.RunAsync(ct => persistence.StartAsync(request, clock.UtcNow, ct), cancellationToken);
        if (result.IsExistingDraft) { state.Begin(ActiveWorkoutLifecycle.NoDraft); return new StartWorkoutResult.ExistingDraft(result.ExistingDraft!); }
        var workout = await WithPreviousAsync(result.Workout!, persistence, executor, cancellationToken);
        state.SetStarted(workout); return new StartWorkoutResult.Started(workout);
    }

    internal static async Task<ActiveWorkoutDto> WithPreviousAsync(ActiveWorkoutDto workout, IWorkoutVerticalSlicePersistence persistence, IOffUiExecutor executor, CancellationToken cancellationToken)
    {
        var exercises = new List<ActiveWorkoutExerciseDto>(workout.Exercises.Count);
        foreach (var exercise in workout.Exercises)
        {
            var previous = await executor.RunAsync(ct => persistence.GetLatestCompletedPerformanceAsync(exercise.ExerciseId, ct), cancellationToken);
            var sets = exercise.Sets.Select(set =>
            {
                var suggestion = previous?.Sets.SingleOrDefault(x => x.SetNumber == set.SetNumber);
                return set with { SuggestedWeightKg = suggestion?.WeightKg, SuggestedReps = suggestion?.Reps };
            }).ToArray();
            exercises.Add(exercise with { PreviousPerformance = previous, Sets = sets });
        }
        return workout with { Exercises = exercises };
    }
}

public sealed class LoadActiveWorkoutUseCase(IWorkoutVerticalSlicePersistence persistence, ActiveWorkoutState state, IOffUiExecutor executor)
{
    public async Task<ActiveWorkoutDto?> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        if (state.CurrentWorkout is not null) return state.CurrentWorkout;
        state.Begin(ActiveWorkoutLifecycle.Rehydrating);
        try
        {
            var draft = await executor.RunAsync(ct => persistence.LoadDraftAsync(ct), cancellationToken);
            if (draft is null) { state.Clear(); return null; }
            draft = await StartWorkoutUseCase.WithPreviousAsync(draft, persistence, executor, cancellationToken);
            state.SetRehydrated(draft); return draft;
        }
        catch { state.Begin(ActiveWorkoutLifecycle.Error); throw; }
    }
}

public sealed class GetPreviousPerformanceUseCase(IWorkoutVerticalSlicePersistence persistence, IOffUiExecutor executor)
{
    public Task<PreviousPerformanceDto?> ExecuteAsync(Guid exerciseId, CancellationToken cancellationToken = default) => executor.RunAsync(ct => persistence.GetLatestCompletedPerformanceAsync(exerciseId, ct), cancellationToken);
}

public sealed class FinishWorkoutUseCase(IWorkoutVerticalSlicePersistence persistence, ActiveWorkoutState state, AutoSaveCoordinator autoSave, IClock clock, IOffUiExecutor executor, IWriteAdmissionGate? writeGate = null)
{
    public async Task<FinishWorkoutResult> ExecuteAsync(CancellationToken cancellationToken = default)
    {
        using var lease = writeGate?.EnterPublicWrite();
        if (!state.SessionId.HasValue) return FinishWorkoutResult.Failure("Active Draftがありません。");
        state.Begin(ActiveWorkoutLifecycle.Finishing);
        try
        {
            await autoSave.FlushAsync(cancellationToken);
            var result = await executor.RunAsync(ct => persistence.FinishAsync(state.SessionId.Value, clock.UtcNow, ct), cancellationToken);
            if (result.Succeeded) state.Clear(); else state.Begin(ActiveWorkoutLifecycle.ActiveDraft);
            return result;
        }
        catch { state.Begin(ActiveWorkoutLifecycle.ActiveDraft); throw; }
    }
}

public sealed class DiscardWorkoutUseCase(IWorkoutVerticalSlicePersistence persistence, ActiveWorkoutState state, AutoSaveCoordinator autoSave, IOffUiExecutor executor, IWriteAdmissionGate? writeGate = null)
{
    public async Task<bool> ExecuteAsync(bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed || !state.SessionId.HasValue) return false;
        using var lease = writeGate?.EnterPublicWrite();
        state.Begin(ActiveWorkoutLifecycle.Discarding);
        try { await autoSave.StopAndDrainAsync(); await executor.RunAsync(ct => persistence.DiscardAsync(state.SessionId.Value, ct), cancellationToken); state.Clear(); return true; }
        catch { state.Begin(ActiveWorkoutLifecycle.ActiveDraft); throw; }
    }
}
