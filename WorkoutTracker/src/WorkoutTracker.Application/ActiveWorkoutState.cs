namespace WorkoutTracker.Application;

public sealed class ActiveWorkoutState
{
    private readonly object _gate = new();
    public event EventHandler? Changed;
    public Guid? SessionId { get; private set; }
    public ActiveWorkoutDto? CurrentWorkout { get; private set; }
    public bool IsActive => SessionId.HasValue && CurrentWorkout is not null;
    public long EditRevision { get; private set; }
    public long LastSavedRevision { get; private set; }
    public WorkoutSaveState SaveState { get; private set; } = WorkoutSaveState.Saved;
    public ActiveWorkoutLifecycle Lifecycle { get; private set; } = ActiveWorkoutLifecycle.NoDraft;
    public string? LastSaveError { get; private set; }
    public bool HasUnsavedChanges => EditRevision != LastSavedRevision;

    public void Begin(ActiveWorkoutLifecycle lifecycle) { lock (_gate) Lifecycle = lifecycle; RaiseChanged(); }
    public void SetStarted(ActiveWorkoutDto workout) => SetBaseline(workout);
    public void SetRehydrated(ActiveWorkoutDto workout) => SetBaseline(workout);
    public long ApplyEdit(ActiveWorkoutDto workout)
    {
        lock (_gate) { CurrentWorkout = workout; SessionId = workout.SessionId; EditRevision++; SaveState = WorkoutSaveState.Dirty; LastSaveError = null; }
        RaiseChanged(); return EditRevision;
    }
    public void MarkSaving(long revision) { lock (_gate) { if (revision <= EditRevision) SaveState = WorkoutSaveState.Saving; } RaiseChanged(); }
    public void MarkSaved(long revision)
    {
        lock (_gate) { LastSavedRevision = Math.Max(LastSavedRevision, revision); SaveState = LastSavedRevision == EditRevision ? WorkoutSaveState.Saved : WorkoutSaveState.Dirty; LastSaveError = null; }
        RaiseChanged();
    }
    public void MarkSaveFailed(string message) { lock (_gate) { SaveState = WorkoutSaveState.Failed; LastSaveError = message; } RaiseChanged(); }
    public void Clear()
    {
        lock (_gate) { SessionId = null; CurrentWorkout = null; EditRevision = 0; LastSavedRevision = 0; SaveState = WorkoutSaveState.Saved; Lifecycle = ActiveWorkoutLifecycle.NoDraft; LastSaveError = null; }
        RaiseChanged();
    }
    private void SetBaseline(ActiveWorkoutDto workout)
    {
        lock (_gate) { SessionId = workout.SessionId; CurrentWorkout = workout; EditRevision = 0; LastSavedRevision = 0; SaveState = WorkoutSaveState.Saved; Lifecycle = ActiveWorkoutLifecycle.ActiveDraft; LastSaveError = null; }
        RaiseChanged();
    }
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

