using WorkoutTracker.Application;

namespace WorkoutTracker.Application.Tests;

public sealed class MaintenanceModeTests
{
    [Fact(DisplayName = "BKP-014-017 Maintenance rejects writes, flushes captured latest revision, keeps active state, and resumes")]
    public async Task Bkp014To017()
    {
        var gate = new WriteAdmissionGate();
        var state = new ActiveWorkoutState();
        var persistence = new FakePersistence();
        var autoSave = new AutoSaveCoordinator(persistence, state, new Clock(), new NeverDelay(), gate);
        var dto = Dto("initial"); state.SetStarted(dto);
        autoSave.NotifyChanged(Dto("revision-1"), Request("revision-1"), false);
        var infrastructure = new FakeBackupInfrastructure();
        var service = new BackupService(gate, autoSave, state, infrastructure);

        var result = await service.CreateBackupAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("revision-1", persistence.Saved.Single().WorkoutName);
        Assert.Equal(state.EditRevision, state.LastSavedRevision);
        Assert.True(state.IsActive); Assert.Equal("revision-1", state.CurrentWorkout!.WorkoutName);
        Assert.True(autoSave.IsQueueEmpty); Assert.Equal(MaintenanceStage.Off, gate.Status.Stage);
        using var resumed = gate.EnterPublicWrite();
        Assert.Equal("backup.db", result.BackupPath);
        Assert.Equal(["checkpoint", "backup", "verify"], infrastructure.Calls);
    }

    [Fact(DisplayName = "BKP-015 public writes are rejected from Entering and permitted after failure cleanup")]
    public async Task Bkp015FailureCleanup()
    {
        var gate = new WriteAdmissionGate();
        var state = new ActiveWorkoutState(); state.SetStarted(Dto("initial"));
        var persistence = new FakePersistence();
        var autoSave = new AutoSaveCoordinator(persistence, state, new Clock(), new NeverDelay(), gate);
        var infrastructure = new FakeBackupInfrastructure { FailBackup = true };
        var service = new BackupService(gate, autoSave, state, infrastructure);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        infrastructure.OnCheckpoint = () => started.TrySetResult(); infrastructure.CheckpointWait = release.Task;

        var task = service.CreateBackupAsync();
        await started.Task;
        Assert.Throws<MaintenanceModeException>(() => gate.EnterPublicWrite());
        release.TrySetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.Equal(MaintenanceStage.Off, gate.Status.Stage);
        using var resumed = gate.EnterPublicWrite();
        Assert.True(state.IsActive);
    }

    private static ActiveWorkoutDto Dto(string name) => new(Guid.Parse("10000000-0000-0000-0000-000000000001"), null, name, new DateOnly(2026, 8, 22), null, []);
    private static SaveDraftRequest Request(string name) => new(Dto(name).SessionId, new DateOnly(2026, 8, 22), name, null, []);
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => new(2026, 8, 22, 0, 0, 0, TimeSpan.Zero); }
    private sealed class NeverDelay : IAutoSaveDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }

    private sealed class FakeBackupInfrastructure : IBackupInfrastructure
    {
        public bool FailBackup { get; set; }
        public Action? OnCheckpoint { get; set; }
        public Task CheckpointWait { get; set; } = Task.CompletedTask;
        public List<string> Calls { get; } = [];
        public Task WaitForConnectionsAndCheckpointAsync(CancellationToken cancellationToken) { Calls.Add("checkpoint"); OnCheckpoint?.Invoke(); return CheckpointWait; }
        public Task<BackupResult> CreateAndValidateBackupAsync(CancellationToken cancellationToken)
        {
            Calls.Add("backup");
            return FailBackup ? Task.FromException<BackupResult>(new InvalidOperationException("injected")) : Task.FromResult(new BackupResult("backup.db", 1, ["AppSettings"], true, true));
        }
        public Task VerifyPrimaryDatabaseAsync(CancellationToken cancellationToken) { Calls.Add("verify"); return Task.CompletedTask; }
    }

    private sealed class FakePersistence : IWorkoutVerticalSlicePersistence
    {
        public List<SaveDraftRequest> Saved { get; } = [];
        public Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) { Saved.Add(request); return Task.CompletedTask; }
        public Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
