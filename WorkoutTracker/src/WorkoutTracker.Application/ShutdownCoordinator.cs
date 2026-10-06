namespace WorkoutTracker.Application;

public enum ShutdownFlushResult
{
    NotRequired,
    Saved,
    TimedOut,
    Failed
}

public sealed class ShutdownCoordinator(AutoSaveCoordinator autoSave, ActiveWorkoutState activeState)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public async Task<ShutdownFlushResult> FlushPendingDraftAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!activeState.IsActive || !activeState.HasUnsavedChanges) return ShutdownFlushResult.NotRequired;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await autoSave.FlushAsync(deadline.Token);
            return ShutdownFlushResult.Saved;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            return ShutdownFlushResult.TimedOut;
        }
        catch
        {
            return ShutdownFlushResult.Failed;
        }
    }
}
