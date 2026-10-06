using System.Collections.Concurrent;
using WorkoutTracker.Application;

namespace WorkoutTracker.Application.Tests;

public sealed class AutoSaveCoordinatorTests
{
    [Fact(DisplayName = "APP-023 Saved Dirty Saving Saved transition updates LastSavedRevision")]
    public async Task App023()
    {
        var h = new Harness(); var states = new List<WorkoutSaveState>(); h.State.Changed += (_, _) => states.Add(h.State.SaveState);
        h.Coordinator.NotifyChanged(h.Dto("edited"), h.Request("edited"), false); Assert.Equal(WorkoutSaveState.Dirty, h.State.SaveState);
        h.Delay.ReleaseLatest(); await WaitUntilAsync(() => h.State.SaveState == WorkoutSaveState.Saved);
        Assert.Contains(WorkoutSaveState.Saving, states); Assert.Equal(1, h.State.LastSavedRevision); Assert.Equal(1, h.Persistence.SaveCount);
    }

    [Fact(DisplayName = "APP-024 and APP-030 save failure retains input and Retry saves latest revision")]
    public async Task App024App030()
    {
        var h = new Harness(); h.Persistence.FailNext = true;
        h.Coordinator.NotifyChanged(h.Dto("kept"), h.Request("kept"), true); await WaitUntilAsync(() => h.State.SaveState == WorkoutSaveState.Failed);
        Assert.Equal("kept", h.State.CurrentWorkout!.WorkoutName); Assert.Equal(1, h.State.EditRevision); Assert.Equal(0, h.State.LastSavedRevision);
        await h.Coordinator.RetryAsync(); Assert.Equal(WorkoutSaveState.Saved, h.State.SaveState); Assert.Equal("kept", h.Persistence.Saved[^1].WorkoutName);
    }

    [Fact(DisplayName = "APP-025 five edits debounce to one latest save after 500ms boundary")]
    public async Task App025()
    {
        var h = new Harness();
        for (var i = 1; i <= 5; i++) h.Coordinator.NotifyChanged(h.Dto($"r{i}"), h.Request($"r{i}"), false);
        Assert.Equal(0, h.Persistence.SaveCount); h.Delay.ReleaseLatest(); await WaitUntilAsync(() => h.State.SaveState == WorkoutSaveState.Saved);
        Assert.Equal(1, h.Persistence.SaveCount); Assert.Equal("r5", h.Persistence.Saved.Single().WorkoutName); Assert.Equal(5, h.State.LastSavedRevision);
    }

    [Fact(DisplayName = "APP-026 immediate save cancels pending debounce callback")]
    public async Task App026()
    {
        var h = new Harness(); h.Coordinator.NotifyChanged(h.Dto("typing"), h.Request("typing"), false);
        h.Coordinator.NotifyChanged(h.Dto("set-added"), h.Request("set-added"), true); h.Delay.ReleaseAll();
        await WaitUntilAsync(() => h.State.SaveState == WorkoutSaveState.Saved); Assert.Equal(1, h.Persistence.SaveCount); Assert.Equal("set-added", h.Persistence.Saved.Single().WorkoutName);
    }

    [Fact(DisplayName = "APP-027 and ASY-004 single-flight keeps current commit and coalesces to latest revision")]
    public async Task App027Asy004()
    {
        var h = new Harness(); h.Persistence.BlockFirst = true;
        h.Coordinator.NotifyChanged(h.Dto("r1"), h.Request("r1"), true); await h.Persistence.FirstStarted.Task;
        h.Coordinator.NotifyChanged(h.Dto("r2"), h.Request("r2"), true); h.Coordinator.NotifyChanged(h.Dto("r3"), h.Request("r3"), true);
        Assert.Equal(1, h.Persistence.MaxConcurrent); h.Persistence.ReleaseFirst.TrySetResult();
        await WaitUntilAsync(() => h.State.SaveState == WorkoutSaveState.Saved && h.State.LastSavedRevision == 3);
        Assert.Equal(1, h.Persistence.MaxConcurrent); Assert.Equal(["r1", "r3"], h.Persistence.Saved.Select(x => x.WorkoutName)); Assert.False(h.Persistence.CommitTokenWasCancelable);
    }

    [Fact(DisplayName = "APP-028 old save completion never marks a newer edit Saved")]
    public async Task App028()
    {
        var h = new Harness(); h.Persistence.BlockFirst = true; var transitions = new List<(WorkoutSaveState State, long Saved, long Edit)>(); h.State.Changed += (_, _) => transitions.Add((h.State.SaveState, h.State.LastSavedRevision, h.State.EditRevision));
        h.Coordinator.NotifyChanged(h.Dto("r1"), h.Request("r1"), true); await h.Persistence.FirstStarted.Task;
        h.Coordinator.NotifyChanged(h.Dto("r2"), h.Request("r2"), true); h.Persistence.ReleaseFirst.TrySetResult();
        await WaitUntilAsync(() => h.State.LastSavedRevision == 2);
        Assert.DoesNotContain(transitions, x => x.State == WorkoutSaveState.Saved && x.Saved == 1 && x.Edit == 2);
    }

    [Fact(DisplayName = "APP-029 flush racing debounce saves a revision only once")]
    public async Task App029()
    {
        var h = new Harness(); h.Persistence.BlockFirst = true; h.Coordinator.NotifyChanged(h.Dto("one"), h.Request("one"), true); await h.Persistence.FirstStarted.Task;
        var flush = h.Coordinator.FlushAsync(); h.Persistence.ReleaseFirst.TrySetResult(); await flush;
        Assert.Equal(1, h.Persistence.SaveCount); Assert.Equal(1, h.State.LastSavedRevision);
    }

    private sealed class Harness
    {
        public Harness()
        {
            State.SetStarted(Dto("initial")); Coordinator = new(Persistence, State, new FixedClock(), Delay);
        }
        public ActiveWorkoutState State { get; } = new();
        public FakePersistence Persistence { get; } = new();
        public ManualDelay Delay { get; } = new();
        public AutoSaveCoordinator Coordinator { get; }
        public ActiveWorkoutDto Dto(string name) => new(Guid.Parse("10000000-0000-0000-0000-000000000001"), null, name, new DateOnly(2026, 8, 22), null, []);
        public SaveDraftRequest Request(string name) => new(Dto(name).SessionId, new DateOnly(2026, 8, 22), name, null, []);
    }

    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 8, 22, 3, 0, 0, TimeSpan.Zero); }

    private sealed class ManualDelay : IAutoSaveDelay
    {
        private readonly ConcurrentQueue<TaskCompletionSource> _waiters = new();
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Assert.Equal(AutoSaveCoordinator.DebounceInterval, delay); var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken)); _waiters.Enqueue(waiter); return waiter.Task;
        }
        public void ReleaseLatest() { var all = Drain(); all.Last().TrySetResult(); }
        public void ReleaseAll() { foreach (var waiter in Drain()) waiter.TrySetResult(); }
        private List<TaskCompletionSource> Drain() { var result = new List<TaskCompletionSource>(); while (_waiters.TryDequeue(out var waiter)) result.Add(waiter); return result; }
    }

    private sealed class FakePersistence : IWorkoutVerticalSlicePersistence
    {
        private int _concurrent;
        public int SaveCount { get; private set; }
        public int MaxConcurrent { get; private set; }
        public bool FailNext { get; set; }
        public bool BlockFirst { get; set; }
        public bool CommitTokenWasCancelable { get; private set; }
        public List<SaveDraftRequest> Saved { get; } = [];
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public async Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrent); MaxConcurrent = Math.Max(MaxConcurrent, concurrent); SaveCount++; CommitTokenWasCancelable |= cancellationToken.CanBeCanceled;
            try
            {
                if (SaveCount == 1) { FirstStarted.TrySetResult(); if (BlockFirst) await ReleaseFirst.Task; }
                if (FailNext) { FailNext = false; throw new InvalidOperationException("injected"); }
                lock (Saved) Saved.Add(request);
            }
            finally { Interlocked.Decrement(ref _concurrent); }
        }
        public Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = DateTime.UtcNow.AddSeconds(5); while (!condition()) { if (DateTime.UtcNow >= timeout) throw new TimeoutException(); await Task.Delay(10); }
    }
}
