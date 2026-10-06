using WorkoutTracker.Application;

namespace WorkoutTracker.Application.Tests;

public sealed class ShutdownCoordinatorTests
{
    [Fact(DisplayName = "ACT-007 pending Auto Save is flushed before normal shutdown without completing or deleting Draft")]
    public async Task PendingDraftIsFlushed()
    {
        var persistence = new ControlledPersistence();
        var state = new ActiveWorkoutState(); state.SetStarted(Dto("saved"));
        var autoSave = new AutoSaveCoordinator(persistence, state, new Clock(), new NeverDelay());
        autoSave.NotifyChanged(Dto("latest"), Request("latest"), false);
        var coordinator = new ShutdownCoordinator(autoSave, state);

        var result = await coordinator.FlushPendingDraftAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ShutdownFlushResult.Saved, result);
        Assert.Equal("latest", Assert.Single(persistence.Saved).WorkoutName);
        Assert.True(state.IsActive); Assert.Equal(WorkoutSaveState.Saved, state.SaveState);
        Assert.Equal(state.EditRevision, state.LastSavedRevision);
        Assert.Equal(TimeSpan.FromSeconds(3), ShutdownCoordinator.DefaultTimeout);
    }

    [Fact(DisplayName = "ACT-007 timeout keeps Draft active and returns control for retry cancel or last-saved exit choice")]
    public async Task TimeoutDoesNotMutateDraftLifecycle()
    {
        var persistence = new ControlledPersistence { BlockSave = true };
        var state = new ActiveWorkoutState(); state.SetStarted(Dto("saved"));
        var autoSave = new AutoSaveCoordinator(persistence, state, new Clock(), new NeverDelay());
        autoSave.NotifyChanged(Dto("latest"), Request("latest"), false);
        var coordinator = new ShutdownCoordinator(autoSave, state);

        var result = await coordinator.FlushPendingDraftAsync(TimeSpan.FromMilliseconds(50));

        Assert.Equal(ShutdownFlushResult.TimedOut, result); Assert.True(state.IsActive);
        Assert.NotEqual(ActiveWorkoutLifecycle.Finishing, state.Lifecycle);
        persistence.ReleaseSave(); await autoSave.FlushAsync();
    }

    [Fact(DisplayName = "ACT-007 failed shutdown flush preserves active Draft and reports failure")]
    public async Task FailedFlushPreservesDraft()
    {
        var persistence = new ControlledPersistence { FailSave = true };
        var state = new ActiveWorkoutState(); state.SetStarted(Dto("saved"));
        var autoSave = new AutoSaveCoordinator(persistence, state, new Clock(), new NeverDelay());
        autoSave.NotifyChanged(Dto("latest"), Request("latest"), false);
        var coordinator = new ShutdownCoordinator(autoSave, state);

        var result = await coordinator.FlushPendingDraftAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(ShutdownFlushResult.Failed, result); Assert.True(state.IsActive);
        Assert.Equal(WorkoutSaveState.Failed, state.SaveState); Assert.True(state.HasUnsavedChanges);
    }

    private static ActiveWorkoutDto Dto(string name) => new(Guid.Parse("10000000-0000-0000-0000-000000000001"), null, name, new DateOnly(2026, 8, 22), null, []);
    private static SaveDraftRequest Request(string name) => new(Dto(name).SessionId, Dto(name).WorkoutDate, name, null, []);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero); }
    private sealed class NeverDelay : IAutoSaveDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }

    private sealed class ControlledPersistence : IWorkoutVerticalSlicePersistence
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool BlockSave { get; set; }
        public bool FailSave { get; set; }
        public List<SaveDraftRequest> Saved { get; } = [];
        public void ReleaseSave() => _release.TrySetResult();
        public async Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            if (BlockSave) await _release.Task;
            if (FailSave) throw new IOException("injected");
            Saved.Add(request);
        }
        public Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
