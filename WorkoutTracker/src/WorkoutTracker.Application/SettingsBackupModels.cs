using WorkoutTracker.Domain;

namespace WorkoutTracker.Application;

public sealed record AppSettingsDto(ThemeMode ThemeMode, string DatabasePath, string BackupDirectory);

public sealed record BackupResult(
    string BackupPath,
    int SchemaVersion,
    IReadOnlyList<string> RequiredTables,
    bool QuickCheckPassed,
    bool WalCheckpointPassed);

public enum MaintenanceStage
{
    Off,
    Entering,
    DrainingWrites,
    FlushingLatestRevision,
    ReleasingConnections,
    Checkpointing,
    BackingUp,
    PreparingRestore,
    ReplacingDatabase,
    RollingBack,
    Validating,
    Reloading,
    Fatal
}

public sealed record MaintenanceStatus(MaintenanceStage Stage, string Message, int ProgressPercent)
{
    public bool IsActive => Stage != MaintenanceStage.Off;
}

public sealed class MaintenanceModeException(string message) : InvalidOperationException(message);

public interface IWriteAdmissionGate
{
    MaintenanceStatus Status { get; }
    event EventHandler? Changed;
    IDisposable EnterPublicWrite();
    void Begin(string reason);
    void SetStage(MaintenanceStage stage, string message, int progressPercent);
    Task WaitForPublicWritesAsync(CancellationToken cancellationToken);
    void End();
}

public sealed record DatabaseValidationResult(
    string Path,
    int SchemaVersion,
    IReadOnlyList<string> MigrationIds,
    IReadOnlyDictionary<string, long> RepresentativeCounts);

public sealed record RestoreWorkFiles(
    Guid OperationId,
    string SelectedBackupPath,
    string EmergencyBackupPath,
    string StagedPath,
    string SwapRollbackPath,
    string FailedPath,
    string WalQuarantinePath,
    string ShmQuarantinePath);

public sealed record RestoreResult(
    bool Succeeded,
    bool RolledBack,
    string EmergencyBackupPath,
    Guid OperationId,
    DatabaseValidationResult RestoredDatabase);

public enum RestoreFailurePoint
{
    ReadSelectedBackup,
    CreateEmergencyBackup,
    CreateStagedDatabase,
    BeforeReplace,
    AfterReplace,
    ValidateRestoredDatabase,
    Rollback
}

public interface IRestoreFailureInjector
{
    void ThrowIfRequested(RestoreFailurePoint point);
}

public interface IRestoreInfrastructure
{
    Task<DatabaseValidationResult> ValidateCandidateAsync(string selectedBackupPath, CancellationToken cancellationToken);
    Task<RestoreWorkFiles> PrepareAsync(string selectedBackupPath, Guid operationId, CancellationToken cancellationToken);
    Task ReplacePrimaryAsync(RestoreWorkFiles files, CancellationToken cancellationToken);
    Task<DatabaseValidationResult> ValidatePrimaryAfterRestoreAsync(CancellationToken cancellationToken);
    Task<DatabaseValidationResult> RollbackAsync(RestoreWorkFiles files, CancellationToken cancellationToken);
    Task CleanupSuccessfulRestoreAsync(RestoreWorkFiles files, CancellationToken cancellationToken);
}

public interface IApplicationStateReloader
{
    Task ReloadAsync(CancellationToken cancellationToken);
}

public sealed class RestoreOperationException(
    string message,
    bool primaryDatabaseRestored,
    bool fatal,
    string? emergencyBackupPath,
    string? failedDatabasePath,
    Exception? innerException = null) : Exception(message, innerException)
{
    public bool PrimaryDatabaseRestored { get; } = primaryDatabaseRestored;
    public bool IsFatal { get; } = fatal;
    public string? EmergencyBackupPath { get; } = emergencyBackupPath;
    public string? FailedDatabasePath { get; } = failedDatabasePath;
}

public interface IAppSettingsStore
{
    Task<AppSettingsDto> GetAsync(CancellationToken cancellationToken);
    Task SaveThemeAsync(ThemeMode themeMode, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken);
}

public interface IBackupInfrastructure
{
    Task WaitForConnectionsAndCheckpointAsync(CancellationToken cancellationToken);
    Task<BackupResult> CreateAndValidateBackupAsync(CancellationToken cancellationToken);
    Task VerifyPrimaryDatabaseAsync(CancellationToken cancellationToken);
}

public sealed class NoOpRestoreFailureInjector : IRestoreFailureInjector
{
    public void ThrowIfRequested(RestoreFailurePoint point) { }
}

public sealed class ApplicationStateReloader(
    ActiveWorkoutState activeWorkout,
    AutoSaveCoordinator autoSave,
    LoadActiveWorkoutUseCase loadActiveWorkout) : IApplicationStateReloader
{
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        autoSave.BeginWorkout();
        activeWorkout.Clear();
        await loadActiveWorkout.ExecuteAsync(cancellationToken);
    }
}

public sealed class MaintenanceCoordinator(
    IWriteAdmissionGate writeGate,
    AutoSaveCoordinator autoSave,
    ActiveWorkoutState activeWorkout,
    IBackupInfrastructure infrastructure,
    IApplicationStateReloader stateReloader)
{
    public MaintenanceStatus Status => writeGate.Status;
    public event EventHandler? StatusChanged { add => writeGate.Changed += value; remove => writeGate.Changed -= value; }

    public async Task EnterAndPrepareAsync(string reason, CancellationToken cancellationToken)
    {
        writeGate.Begin(reason);
        autoSave.PausePublicWrites();
        writeGate.SetStage(MaintenanceStage.DrainingWrites, "実行中の書込みを完了しています…", 10);
        await writeGate.WaitForPublicWritesAsync(cancellationToken);
        await autoSave.WaitForRunningSaveAsync(cancellationToken);
        var capturedRevision = activeWorkout.EditRevision;
        writeGate.SetStage(MaintenanceStage.FlushingLatestRevision, "最新のWorkoutを保存しています…", 25);
        await autoSave.FlushForMaintenanceAsync(capturedRevision, cancellationToken);
        if (!autoSave.IsQueueEmpty || activeWorkout.LastSavedRevision != capturedRevision)
            throw new InvalidOperationException("最新Revisionの保存を確認できませんでした。");
        writeGate.SetStage(MaintenanceStage.ReleasingConnections, "Database接続を解放しています…", 40);
        writeGate.SetStage(MaintenanceStage.Checkpointing, "WALを安全に確定しています…", 50);
        await infrastructure.WaitForConnectionsAndCheckpointAsync(cancellationToken);
    }

    public void SetStage(MaintenanceStage stage, string message, int progress) => writeGate.SetStage(stage, message, progress);

    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        writeGate.SetStage(MaintenanceStage.Reloading, "Application Stateを再構築しています…", 95);
        await infrastructure.VerifyPrimaryDatabaseAsync(cancellationToken);
        await stateReloader.ReloadAsync(cancellationToken);
        autoSave.Resume();
        writeGate.End();
    }

    public async Task CompleteAfterSafeFailureAsync(CancellationToken cancellationToken)
    {
        await CompleteAsync(cancellationToken);
    }

    public void FailFatal(string message)
    {
        writeGate.SetStage(MaintenanceStage.Fatal, message, 100);
    }
}

public sealed class WriteAdmissionGate : IWriteAdmissionGate
{
    private readonly object _sync = new();
    private int _activeWrites;
    private TaskCompletionSource _drained = CompletedSource();
    private MaintenanceStatus _status = new(MaintenanceStage.Off, string.Empty, 0);

    public event EventHandler? Changed;
    public MaintenanceStatus Status { get { lock (_sync) return _status; } }

    public IDisposable EnterPublicWrite()
    {
        lock (_sync)
        {
            if (_status.IsActive) throw new MaintenanceModeException("メンテナンス中のため書込みを受け付けられません。");
            if (_activeWrites++ == 0) _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new Lease(this);
        }
    }

    public void Begin(string reason)
    {
        lock (_sync)
        {
            if (_status.IsActive) throw new InvalidOperationException("Maintenance Mode is already active.");
            _status = new(MaintenanceStage.Entering, reason, 0);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetStage(MaintenanceStage stage, string message, int progressPercent)
    {
        lock (_sync)
        {
            if (!_status.IsActive) throw new InvalidOperationException("Maintenance Mode is not active.");
            _status = new(stage, message, Math.Clamp(progressPercent, 0, 100));
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task WaitForPublicWritesAsync(CancellationToken cancellationToken)
    {
        Task task;
        lock (_sync) task = _activeWrites == 0 ? Task.CompletedTask : _drained.Task;
        return task.WaitAsync(cancellationToken);
    }

    public void End()
    {
        lock (_sync) _status = new(MaintenanceStage.Off, string.Empty, 0);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Release()
    {
        lock (_sync)
        {
            if (--_activeWrites == 0) _drained.TrySetResult();
        }
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    private sealed class Lease(WriteAdmissionGate owner) : IDisposable
    {
        private WriteAdmissionGate? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

public sealed class SettingsService(IAppSettingsStore store, IClock clock, IOffUiExecutor executor, IWriteAdmissionGate writeGate)
{
    public Task<AppSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
        executor.RunAsync(store.GetAsync, cancellationToken);

    public Task SaveThemeAsync(ThemeMode themeMode, CancellationToken cancellationToken = default) =>
        executor.RunAsync(async token =>
        {
            using var lease = writeGate.EnterPublicWrite();
            await store.SaveThemeAsync(themeMode, clock.UtcNow, token);
        }, cancellationToken);
}

public sealed class BackupService(
    IWriteAdmissionGate writeGate,
    AutoSaveCoordinator autoSave,
    ActiveWorkoutState activeWorkout,
    IBackupInfrastructure infrastructure,
    IApplicationStateReloader? stateReloader = null)
{
    private readonly MaintenanceCoordinator _maintenance = new(writeGate, autoSave, activeWorkout, infrastructure, stateReloader ?? new PassThroughReloader());
    public MaintenanceStatus Status => _maintenance.Status;
    public event EventHandler? StatusChanged { add => _maintenance.StatusChanged += value; remove => _maintenance.StatusChanged -= value; }

    public async Task<BackupResult> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var entered = false;
        try
        {
            await _maintenance.EnterAndPrepareAsync("Backupを安全に作成しています。", cancellationToken); entered = true;
            _maintenance.SetStage(MaintenanceStage.BackingUp, "Backupを作成しています…", 65);
            var result = await infrastructure.CreateAndValidateBackupAsync(cancellationToken);
            _maintenance.SetStage(MaintenanceStage.Validating, "Backupの整合性を確認しました。", 90);
            await _maintenance.CompleteAsync(cancellationToken);
            return result;
        }
        catch
        {
            if (writeGate.Status.IsActive)
            {
                if (entered) await _maintenance.CompleteAfterSafeFailureAsync(CancellationToken.None);
                else { autoSave.Resume(); writeGate.End(); }
            }
            throw;
        }
    }

    private sealed class PassThroughReloader : IApplicationStateReloader { public Task ReloadAsync(CancellationToken cancellationToken) => Task.CompletedTask; }
}

public sealed class RestoreService(
    IWriteAdmissionGate writeGate,
    AutoSaveCoordinator autoSave,
    ActiveWorkoutState activeWorkout,
    IBackupInfrastructure maintenanceInfrastructure,
    IRestoreInfrastructure restoreInfrastructure,
    IApplicationStateReloader stateReloader)
{
    private readonly MaintenanceCoordinator _maintenance = new(writeGate, autoSave, activeWorkout, maintenanceInfrastructure, stateReloader);
    public MaintenanceStatus Status => _maintenance.Status;
    public event EventHandler? StatusChanged { add => _maintenance.StatusChanged += value; remove => _maintenance.StatusChanged -= value; }

    public Task<DatabaseValidationResult> ValidateAsync(string path, CancellationToken cancellationToken = default) =>
        restoreInfrastructure.ValidateCandidateAsync(path, cancellationToken);

    public async Task<RestoreResult> RestoreAsync(string selectedBackupPath, bool confirmed, CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new InvalidOperationException("Restore confirmation is required.");
        await restoreInfrastructure.ValidateCandidateAsync(selectedBackupPath, cancellationToken);
        RestoreWorkFiles? files = null;
        var replacementStarted = false;
        var maintenanceEntered = false;
        try
        {
            await _maintenance.EnterAndPrepareAsync("Restoreのため現在のDatabaseを安全に退避しています。", cancellationToken);
            maintenanceEntered = true;
            _maintenance.SetStage(MaintenanceStage.PreparingRestore, "Emergency Backupとstaged Databaseを作成しています…", 60);
            files = await restoreInfrastructure.PrepareAsync(selectedBackupPath, Guid.NewGuid(), cancellationToken);
            _maintenance.SetStage(MaintenanceStage.ReplacingDatabase, "Primary Databaseを置換しています…", 75);
            replacementStarted = true;
            await restoreInfrastructure.ReplacePrimaryAsync(files, CancellationToken.None);
            _maintenance.SetStage(MaintenanceStage.Validating, "復元Databaseを検証しています…", 88);
            var validation = await restoreInfrastructure.ValidatePrimaryAfterRestoreAsync(CancellationToken.None);
            await _maintenance.CompleteAsync(CancellationToken.None);
            await restoreInfrastructure.CleanupSuccessfulRestoreAsync(files, CancellationToken.None);
            return new(true, false, files.EmergencyBackupPath, files.OperationId, validation);
        }
        catch (Exception restoreError)
        {
            if (!maintenanceEntered) throw;
            if (files is null || !replacementStarted)
            {
                try
                {
                    await _maintenance.CompleteAfterSafeFailureAsync(CancellationToken.None);
                    throw new RestoreOperationException("RestoreはDB置換前に失敗しました。現在のDatabaseは変更されていません。", true, false, files?.EmergencyBackupPath, files?.FailedPath, restoreError);
                }
                catch (RestoreOperationException) { throw; }
                catch (Exception verifyError)
                {
                    _maintenance.FailFatal("Primary Databaseの安全性を確認できません。通常操作を停止しました。");
                    throw new RestoreOperationException("Restore準備失敗後にPrimary Databaseを再確認できませんでした。", false, true, files?.EmergencyBackupPath, files?.FailedPath, new AggregateException(restoreError, verifyError));
                }
            }

            _maintenance.SetStage(MaintenanceStage.RollingBack, "Restore失敗を検出しました。元のDatabaseへ戻しています…", 92);
            try
            {
                var recovered = await restoreInfrastructure.RollbackAsync(files, CancellationToken.None);
                await _maintenance.CompleteAfterSafeFailureAsync(CancellationToken.None);
                throw new RestoreOperationException($"Restoreに失敗しましたが、元のDatabaseへ復旧しました（Schema v{recovered.SchemaVersion}）。", true, false, files.EmergencyBackupPath, files.FailedPath, restoreError);
            }
            catch (RestoreOperationException) { throw; }
            catch (Exception rollbackError)
            {
                _maintenance.FailFatal($"Rollbackにも失敗しました。Emergency Backup: {files.EmergencyBackupPath} / Failed DB: {files.FailedPath}");
                throw new RestoreOperationException("RestoreとRollbackの両方に失敗しました。通常操作を停止しています。", false, true, files.EmergencyBackupPath, files.FailedPath, new AggregateException(restoreError, rollbackError));
            }
        }
    }
}
