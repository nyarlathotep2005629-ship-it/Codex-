using WorkoutTracker.Application;

namespace WorkoutTracker.Application.Tests;

public sealed class WorkoutUseCaseStateTests
{
    [Fact(DisplayName = "APP-035 Active State clears only after Finish commit succeeds")]
    public async Task App035()
    {
        var h = new Harness(); var useCase = new FinishWorkoutUseCase(h.Persistence, h.State, h.AutoSave, h.Clock, new InlineExecutor());
        var execution = useCase.ExecuteAsync(); await h.Persistence.FinishStarted.Task;
        Assert.True(h.State.IsActive); Assert.Equal(ActiveWorkoutLifecycle.Finishing, h.State.Lifecycle);
        h.Persistence.FinishRelease.TrySetResult(FinishWorkoutResult.Success()); var result = await execution;
        Assert.True(result.Succeeded); Assert.False(h.State.IsActive); Assert.Equal(ActiveWorkoutLifecycle.NoDraft, h.State.Lifecycle);
    }

    [Fact(DisplayName = "APP-038 Active State clears only after Discard commit succeeds")]
    public async Task App038()
    {
        var h = new Harness(); var useCase = new DiscardWorkoutUseCase(h.Persistence, h.State, h.AutoSave, new InlineExecutor());
        var execution = useCase.ExecuteAsync(true); await h.Persistence.DiscardStarted.Task;
        Assert.True(h.State.IsActive); Assert.Equal(ActiveWorkoutLifecycle.Discarding, h.State.Lifecycle);
        h.Persistence.DiscardRelease.TrySetResult(); Assert.True(await execution); Assert.False(h.State.IsActive);
    }

    private sealed class Harness
    {
        public Harness()
        {
            State.SetStarted(new(Guid.NewGuid(), null, "Workout", new DateOnly(2026, 8, 22), null, [])); AutoSave = new(Persistence, State, Clock, new ImmediateDelay());
        }
        public ActiveWorkoutState State { get; } = new();
        public GatePersistence Persistence { get; } = new();
        public FixedClock Clock { get; } = new();
        public AutoSaveCoordinator AutoSave { get; }
    }
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 8, 22, 3, 0, 0, TimeSpan.Zero); }
    private sealed class ImmediateDelay : IAutoSaveDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask; }
    private sealed class InlineExecutor : IOffUiExecutor
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class GatePersistence : IWorkoutVerticalSlicePersistence
    {
        public TaskCompletionSource FinishStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<FinishWorkoutResult> FinishRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DiscardStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DiscardRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) { FinishStarted.TrySetResult(); return FinishRelease.Task; }
        public Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) { DiscardStarted.TrySetResult(); return DiscardRelease.Task; }
    }
}
