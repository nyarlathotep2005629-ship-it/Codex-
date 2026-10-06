namespace WorkoutTracker.Application;

public sealed class SystemAutoSaveDelay : IAutoSaveDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

public sealed class AutoSaveCoordinator(IWorkoutVerticalSlicePersistence persistence, ActiveWorkoutState state, IClock clock, IAutoSaveDelay delay, IWriteAdmissionGate? writeGate = null)
{
    public static readonly TimeSpan DebounceInterval = TimeSpan.FromMilliseconds(500);
    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCancellation;
    private SaveEnvelope? _pending;
    private SaveEnvelope? _latest;
    private Task? _worker;
    private long? _savingRevision;
    private bool _accepting = true;

    public void Resume() { lock (_gate) _accepting = true; }
    public bool IsQueueEmpty { get { lock (_gate) return _worker is null && _pending is null && _debounceCancellation is null; } }
    public void BeginWorkout()
    {
        lock (_gate)
        {
            if (_worker is { IsCompleted: false }) throw new InvalidOperationException("Cannot replace the active workout while a save is running.");
            CancelDebounceLocked(); _pending = null; _latest = null; _worker = null; _savingRevision = null; _accepting = true;
        }
    }

    public long NotifyChanged(ActiveWorkoutDto currentWorkout, SaveDraftRequest payload, bool immediate)
    {
        lock (_gate)
        {
            if (!_accepting || writeGate?.Status.IsActive == true) throw new MaintenanceModeException("メンテナンス中のためWorkoutを変更できません。");
            var revision = state.ApplyEdit(currentWorkout);
            var envelope = new SaveEnvelope(revision, payload);
            _latest = envelope; _pending = envelope;
            CancelDebounceLocked();
            if (immediate) StartWorkerLocked();
            else
            {
                _debounceCancellation = new CancellationTokenSource();
                _ = DebounceAsync(revision, _debounceCancellation.Token);
            }
            return revision;
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? worker;
            lock (_gate)
            {
                CancelDebounceLocked();
                if (state.HasUnsavedChanges && (_latest is null || _latest.Revision < state.EditRevision)) throw new InvalidOperationException("The latest edit is not valid for persistence.");
                if (_latest is not null && state.LastSavedRevision < _latest.Revision && _savingRevision != _latest.Revision && _pending?.Revision != _latest.Revision) _pending = _latest;
                StartWorkerLocked(); worker = _worker;
            }
            if (worker is null) return;
            await worker.WaitAsync(cancellationToken);
            if (state.SaveState == WorkoutSaveState.Failed) throw new InvalidOperationException(state.LastSaveError ?? "Auto Save failed.");
            lock (_gate) if (_worker is null && _pending is null && state.LastSavedRevision == state.EditRevision) return;
        }
    }

    public async Task RetryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { if (_latest is not null) _pending = _latest; StartWorkerLocked(); }
        await FlushAsync(cancellationToken);
    }

    public async Task StopAndDrainAsync()
    {
        Task? running;
        lock (_gate) { _accepting = false; CancelDebounceLocked(); _pending = null; running = _worker; }
        if (running is not null) await running;
    }

    public void PausePublicWrites()
    {
        lock (_gate) { _accepting = false; CancelDebounceLocked(); }
    }

    public Task WaitForRunningSaveAsync(CancellationToken cancellationToken = default)
    {
        Task? running;
        lock (_gate) running = _worker;
        return running is null ? Task.CompletedTask : running.WaitAsync(cancellationToken);
    }

    public async Task FlushForMaintenanceAsync(long capturedRevision, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task? worker;
            lock (_gate)
            {
                CancelDebounceLocked();
                if (capturedRevision == state.LastSavedRevision && _worker is null) return;
                if (_latest is null || _latest.Revision < capturedRevision)
                    throw new InvalidOperationException("Captured Revisionの保存payloadがありません。");
                if (_latest.Revision == capturedRevision && _savingRevision != capturedRevision) _pending = _latest;
                StartWorkerLocked();
                worker = _worker;
            }
            if (worker is null) throw new InvalidOperationException("Maintenance flushを開始できませんでした。");
            await worker.WaitAsync(cancellationToken);
            if (state.SaveState == WorkoutSaveState.Failed) throw new InvalidOperationException(state.LastSaveError ?? "Auto Save failed.");
            lock (_gate) if (_worker is null && _pending is null && state.LastSavedRevision == capturedRevision) return;
        }
    }

    private async Task DebounceAsync(long revision, CancellationToken cancellationToken)
    {
        try { await delay.DelayAsync(DebounceInterval, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        lock (_gate)
        {
            if (cancellationToken.IsCancellationRequested || _pending?.Revision != revision) return;
            _debounceCancellation?.Dispose(); _debounceCancellation = null; StartWorkerLocked();
        }
    }

    private void StartWorkerLocked()
    {
        if (_pending is null || _worker is { IsCompleted: false }) return;
        _worker = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        while (true)
        {
            SaveEnvelope? item;
            lock (_gate) { item = _pending; _pending = null; _savingRevision = item?.Revision; }
            if (item is null) { lock (_gate) _worker = null; return; }
            state.MarkSaving(item.Revision);
            try
            {
                await Task.Run(() => persistence.SaveDraftAsync(item.Payload, clock.UtcNow, CancellationToken.None));
                state.MarkSaved(item.Revision);
                lock (_gate) if (_savingRevision == item.Revision) _savingRevision = null;
            }
            catch (Exception ex)
            {
                state.MarkSaveFailed("保存できませんでした。再試行してください。");
                lock (_gate) { _pending = null; _worker = null; _savingRevision = null; }
                _ = ex;
                return;
            }
        }
    }

    private void CancelDebounceLocked()
    {
        if (_debounceCancellation is null) return;
        _debounceCancellation.Cancel(); _debounceCancellation.Dispose(); _debounceCancellation = null;
    }

    private sealed record SaveEnvelope(long Revision, SaveDraftRequest Payload);
}
