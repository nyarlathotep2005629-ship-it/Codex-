namespace WorkoutTracker.Mobile;

internal sealed class AsyncOperationGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    public bool IsRunning => _semaphore.CurrentCount == 0;

    public async Task<bool> TryRunAsync(Func<Task> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!await _semaphore.WaitAsync(0)) return false;
        try
        {
            await operation();
            return true;
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
