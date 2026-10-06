using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;

namespace WorkoutTracker.Infrastructure.Persistence;

public enum MigrationFailurePoint
{
    CreateEmergencyBackup,
    WritePreparedManifest,
    ExecuteMigration,
    ValidateMigratedDatabase,
    RecoverEmergencyBackup
}

public interface IMigrationFailureInjector
{
    void ThrowIfRequested(MigrationFailurePoint point);
}

public static class MigrationSafetyLimits
{
    public static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(120);
}

public interface IMigrationProcessLockProvider
{
    IDisposable? TryAcquire(string databasePath, TimeSpan timeout);
}

internal sealed class NamedMigrationProcessLockProvider : IMigrationProcessLockProvider
{
    public IDisposable? TryAcquire(string databasePath, TimeSpan timeout)
    {
        var mutex = new Mutex(false, CreateMutexName(databasePath));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { mutex.Dispose(); return null; }
            return new MutexLease(mutex);
        }
        catch
        {
            if (!acquired) mutex.Dispose();
            throw;
        }
    }

    private static string CreateMutexName(string databasePath)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(databasePath).ToUpperInvariant())));
        return $"Local\\WorkoutTracker.Migration.{hash[..24]}";
    }

    private sealed class MutexLease(Mutex mutex) : IDisposable
    {
        private Mutex? _mutex = mutex;
        public void Dispose()
        {
            var owned = Interlocked.Exchange(ref _mutex, null);
            if (owned is null) return;
            owned.ReleaseMutex(); owned.Dispose();
        }
    }
}

internal sealed class NoOpMigrationFailureInjector : IMigrationFailureInjector
{
    public void ThrowIfRequested(MigrationFailurePoint point) { }
}

public sealed class StartupRecoveryException(
    string safeMessage,
    Guid correlationId,
    string? emergencyBackupPath,
    string? manifestPath,
    bool databaseRecovered,
    bool fatal,
    Exception? innerException = null) : Exception(safeMessage, innerException)
{
    public Guid CorrelationId { get; } = correlationId;
    public string? EmergencyBackupPath { get; } = emergencyBackupPath;
    public string? ManifestPath { get; } = manifestPath;
    public bool DatabaseRecovered { get; } = databaseRecovered;
    public bool IsFatal { get; } = fatal;

    public string ToSafeUserMessage() =>
        $"{Message}\n\nCorrelation ID: {CorrelationId}\nEmergency Backup: {EmergencyBackupPath ?? "なし"}";
}

internal sealed record MigrationOperationManifest(
    Guid OperationId,
    int FromVersion,
    int ToVersion,
    string BackupPath,
    long StartedAtUtcTicks,
    string State,
    string[] MigrationHistory);

internal interface IStartupMigrationCoordinator
{
    Task InitializeAsync(CancellationToken cancellationToken);
}

internal sealed class StartupMigrationCoordinator(
    IDbContextFactory<WorkoutTrackerDbContext> factory,
    DatabaseLocation location,
    IMigrationFailureInjector failureInjector,
    IMigrationProcessLockProvider processLockProvider) : IStartupMigrationCoordinator
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(MigrationSafetyLimits.OverallTimeout);
        await Task.Run(() =>
        {
            using var processLock = processLockProvider.TryAcquire(location.DatabasePath, MigrationSafetyLimits.LockTimeout);
            if (processLock is null) throw new StartupRecoveryException("別のWorkout Tracker instanceがMigrationを実行中です。終了後に再試行してください。", Guid.NewGuid(), null, null, false, false);
            try
            {
                InitializeWithinMutexAsync(timeout.Token).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException ex) when (timeout.IsCancellationRequested)
            {
                throw new StartupRecoveryException("Database Migrationがtimeoutまたは中断されました。通常起動を停止しました。", Guid.NewGuid(), null, null, false, false, ex);
            }
        }, timeout.Token);
    }

    private async Task InitializeWithinMutexAsync(CancellationToken cancellationToken)
    {
        await RecoverIncompleteOperationAsync(cancellationToken);
        await ApplyPendingMigrationsAsync(cancellationToken);
        await EnsureSettingsAsync(cancellationToken);
    }

    private async Task ApplyPendingMigrationsAsync(CancellationToken cancellationToken)
    {
        string[] pending;
        int applicationTableCount;
        await using (var preflight = await factory.CreateDbContextAsync(cancellationToken))
        {
            pending = (await preflight.Database.GetPendingMigrationsAsync(cancellationToken)).ToArray();
            applicationTableCount = pending.Length == 0 ? 0 : await GetApplicationTableCountAsync(preflight, cancellationToken);
        }
        if (pending.Length == 0)
        {
            if (File.Exists(location.DatabasePath)) await ValidateCurrentDatabaseAsync(cancellationToken);
            return;
        }

        var current = await ReadSchemaStateAsync(location.DatabasePath, cancellationToken);
        if (applicationTableCount == 0 && current.Version == 0 && current.History.Length == 0)
        {
            await using var fresh = await factory.CreateDbContextAsync(cancellationToken);
            await fresh.Database.MigrateAsync(cancellationToken);
            await ValidateCurrentDatabaseAsync(cancellationToken);
            return;
        }

        if (current.Version < 0 || current.Version >= RestoreInfrastructure.CurrentSchemaVersion)
            throw new StartupRecoveryException("現在のDatabase schemaは自動Migration対象として認識できません。", Guid.NewGuid(), null, null, false, true);

        var operationId = Guid.NewGuid();
        var emergencyDirectory = Path.Combine(Path.GetDirectoryName(location.DatabasePath)!, "Backups", "Migration");
        var maintenanceDirectory = Path.Combine(Path.GetDirectoryName(location.DatabasePath)!, "Maintenance");
        Directory.CreateDirectory(emergencyDirectory); Directory.CreateDirectory(maintenanceDirectory);
        var backupPath = Path.Combine(emergencyDirectory, $"WorkoutTracker_PreMigration_{current.Version}_{RestoreInfrastructure.CurrentSchemaVersion}_{DateTime.Now:yyyyMMdd_HHmmss}_{operationId}.db");
        var manifestPath = Path.Combine(maintenanceDirectory, $"MigrationOperation_{operationId}.json");
        var manifest = new MigrationOperationManifest(operationId, current.Version, RestoreInfrastructure.CurrentSchemaVersion, backupPath, DateTimeOffset.UtcNow.UtcTicks, "Prepared", current.History);

        try
        {
            failureInjector.ThrowIfRequested(MigrationFailurePoint.CreateEmergencyBackup);
            await BackupDatabaseAsync(location.DatabasePath, backupPath, cancellationToken);
            await ValidateSchemaStateAsync(backupPath, current.Version, current.History, cancellationToken);
            failureInjector.ThrowIfRequested(MigrationFailurePoint.WritePreparedManifest);
            await WriteManifestAsync(manifestPath, manifest, cancellationToken);
        }
        catch (Exception preparationError)
        {
            try
            {
                await ValidateSchemaStateAsync(location.DatabasePath, current.Version, current.History, CancellationToken.None);
                throw new StartupRecoveryException("Migration準備に失敗しました。現在のDatabaseは変更されていません。", operationId, File.Exists(backupPath) ? backupPath : null, manifestPath, true, false, preparationError);
            }
            catch (StartupRecoveryException) { throw; }
            catch (Exception validationError)
            {
                throw new StartupRecoveryException("Migration準備失敗後に現在のDatabase整合性を証明できません。通常操作を停止します。", operationId, File.Exists(backupPath) ? backupPath : null, manifestPath, false, true, new AggregateException(preparationError, validationError));
            }
        }

        try
        {
            failureInjector.ThrowIfRequested(MigrationFailurePoint.ExecuteMigration);
            await using (var migration = await factory.CreateDbContextAsync(cancellationToken))
                await migration.Database.MigrateAsync(cancellationToken);
            failureInjector.ThrowIfRequested(MigrationFailurePoint.ValidateMigratedDatabase);
            await ValidateCurrentDatabaseAsync(cancellationToken);
            await WriteManifestAsync(manifestPath, manifest with { State = "Completed" }, cancellationToken);
        }
        catch (Exception migrationError)
        {
            var recovered = false;
            try
            {
                var after = await ReadSchemaStateAsync(location.DatabasePath, CancellationToken.None);
                if (after.Version == current.Version && after.History.SequenceEqual(current.History))
                {
                    await ValidateSchemaStateAsync(location.DatabasePath, current.Version, current.History, CancellationToken.None);
                    recovered = true;
                }
                else
                {
                    failureInjector.ThrowIfRequested(MigrationFailurePoint.RecoverEmergencyBackup);
                    await RestoreEmergencyAsync(backupPath, CancellationToken.None);
                    await ValidateSchemaStateAsync(location.DatabasePath, current.Version, current.History, CancellationToken.None);
                    recovered = true;
                }
                await WriteManifestAsync(manifestPath, manifest with { State = "Recovered" }, CancellationToken.None);
            }
            catch (Exception recoveryError)
            {
                throw new StartupRecoveryException("Migration失敗後のDatabase復旧に失敗しました。通常操作を停止します。", operationId, backupPath, manifestPath, false, true, new AggregateException(migrationError, recoveryError));
            }
            throw new StartupRecoveryException("Migrationに失敗しました。旧Databaseは復旧済みです。再起動して再試行してください。", operationId, backupPath, manifestPath, recovered, false, migrationError);
        }
    }

    private async Task RecoverIncompleteOperationAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetDirectoryName(location.DatabasePath)!, "Maintenance");
        if (!Directory.Exists(directory)) return;
        foreach (var path in Directory.EnumerateFiles(directory, "MigrationOperation_*.json"))
        {
            var manifest = JsonSerializer.Deserialize<MigrationOperationManifest>(await File.ReadAllTextAsync(path, cancellationToken));
            if (manifest is null || manifest.State is "Completed" or "Recovered") continue;
            try
            {
                var current = await ReadSchemaStateAsync(location.DatabasePath, cancellationToken);
                if (current.Version == manifest.ToVersion)
                {
                    await ValidateCurrentDatabaseAsync(cancellationToken);
                    await WriteManifestAsync(path, manifest with { State = "Completed" }, cancellationToken);
                    continue;
                }
                if (current.Version == manifest.FromVersion && current.History.SequenceEqual(manifest.MigrationHistory))
                {
                    await ValidateSchemaStateAsync(location.DatabasePath, manifest.FromVersion, manifest.MigrationHistory, cancellationToken);
                    await RemoveAbandonedEfLockRowAsync(cancellationToken);
                    await WriteManifestAsync(path, manifest with { State = "Recovered" }, cancellationToken);
                    throw new StartupRecoveryException("中断されたMigrationを検出し、旧Databaseの整合性を確認しました。再起動してください。", manifest.OperationId, manifest.BackupPath, path, true, false);
                }
                if (!File.Exists(manifest.BackupPath)) throw new FileNotFoundException("Migration Emergency Backupがありません。", manifest.BackupPath);
                await RestoreEmergencyAsync(manifest.BackupPath, cancellationToken);
                await ValidateSchemaStateAsync(location.DatabasePath, manifest.FromVersion, manifest.MigrationHistory, cancellationToken);
                await WriteManifestAsync(path, manifest with { State = "Recovered" }, cancellationToken);
                throw new StartupRecoveryException("中断されたMigrationから旧Databaseへ復旧しました。再起動してください。", manifest.OperationId, manifest.BackupPath, path, true, false);
            }
            catch (StartupRecoveryException) { throw; }
            catch (Exception ex)
            {
                throw new StartupRecoveryException("中断Migrationの状態を安全に判定できません。通常操作を停止します。", manifest.OperationId, manifest.BackupPath, path, false, true, ex);
            }
        }
    }

    private async Task RemoveAbandonedEfLockRowAsync(CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection(location.DatabasePath, SqliteOpenMode.ReadWrite);
        await connection.OpenAsync(cancellationToken);
        await using var exists = connection.CreateCommand();
        exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsLock';";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return;
        await using var transaction = connection.BeginTransaction(System.Data.IsolationLevel.Serializable);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "DELETE FROM __EFMigrationsLock;";
        await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    private async Task RestoreEmergencyAsync(string backupPath, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        var maintenance = Path.Combine(Path.GetDirectoryName(location.DatabasePath)!, "Maintenance");
        Directory.CreateDirectory(maintenance);
        var recoveryId = Guid.NewGuid();
        var staged = Path.Combine(maintenance, $"MigrationRecovery_{recoveryId}.staged.db");
        var failed = Path.Combine(maintenance, $"MigrationFailed_{recoveryId}.db");
        await BackupDatabaseAsync(backupPath, staged, cancellationToken);
        await ValidateSchemaStateAsync(staged, (await ReadSchemaStateAsync(backupPath, cancellationToken)).Version, (await ReadSchemaStateAsync(backupPath, cancellationToken)).History, cancellationToken);
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var sidecar = location.DatabasePath + suffix;
            if (File.Exists(sidecar)) File.Move(sidecar, failed + suffix, true);
        }
        if (File.Exists(location.DatabasePath)) File.Replace(staged, location.DatabasePath, failed, true); else File.Move(staged, location.DatabasePath);
        SqliteConnection.ClearAllPools();
    }

    private static async Task BackupDatabaseAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = CreateConnection(sourcePath, SqliteOpenMode.ReadOnly);
        await using var destination = CreateConnection(destinationPath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken); await destination.OpenAsync(cancellationToken); source.BackupDatabase(destination);
    }

    private async Task ValidateCurrentDatabaseAsync(CancellationToken cancellationToken)
    {
        var current = await ReadSchemaStateAsync(location.DatabasePath, cancellationToken);
        if (current.Version != RestoreInfrastructure.CurrentSchemaVersion || current.History.Length != 1 || current.History[0] != RestoreInfrastructure.CurrentMigrationId)
            throw new InvalidOperationException("Schema version and EF Migration history do not match the current application.");
        await ValidateSchemaStateAsync(location.DatabasePath, current.Version, current.History, cancellationToken);
        await using var connection = CreateConnection(location.DatabasePath, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        foreach (var table in new[] { "AppSettings", "MuscleGroups", "Exercises", "WorkoutMenus", "WorkoutSessions", "WorkoutExercises", "WorkoutSets" })
        {
            await using var command = connection.CreateCommand(); command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;"; command.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1) throw new InvalidOperationException($"Required table is missing: {table}.");
        }
        await using var fk = connection.CreateCommand(); fk.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await fk.ExecuteReaderAsync(cancellationToken); if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("foreign_key_check failed.");
    }

    private static async Task ValidateSchemaStateAsync(string path, int expectedVersion, string[] expectedHistory, CancellationToken cancellationToken)
    {
        var state = await ReadSchemaStateAsync(path, cancellationToken);
        if (state.Version != expectedVersion || !state.History.SequenceEqual(expectedHistory)) throw new InvalidOperationException("Database schema state does not match the recorded state.");
    }

    private static async Task<(int Version, string[] History)> ReadSchemaStateAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return (0, []);
        await using var connection = CreateConnection(path, SqliteOpenMode.ReadOnly); await connection.OpenAsync(cancellationToken);
        await using (var quick = connection.CreateCommand()) { quick.CommandText = "PRAGMA quick_check;"; if (!string.Equals(Convert.ToString(await quick.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("quick_check failed."); }
        await using var versionCommand = connection.CreateCommand(); versionCommand.CommandText = "PRAGMA user_version;"; var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        await using var exists = connection.CreateCommand(); exists.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory';";
        if (Convert.ToInt32(await exists.ExecuteScalarAsync(cancellationToken)) == 0) return (version, []);
        var history = new List<string>(); await using var command = connection.CreateCommand(); command.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;"; await using var reader = await command.ExecuteReaderAsync(cancellationToken); while (await reader.ReadAsync(cancellationToken)) history.Add(reader.GetString(0));
        return (version, history.ToArray());
    }

    private static async Task<int> GetApplicationTableCountAsync(WorkoutTrackerDbContext db, CancellationToken cancellationToken)
    {
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('__EFMigrationsHistory','__EFMigrationsLock');";
            return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task EnsureSettingsAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO AppSettings
                (Id, ThemeMode, DashboardPeriod, DashboardExerciseIdsJson, WindowLeft, WindowTop, WindowWidth, WindowHeight, WindowState, UpdatedAtUtcTicks)
            VALUES (1, 'System', 'ThreeMonths', '[]', NULL, NULL, NULL, NULL, 'Normal', 0)
            ON CONFLICT(Id) DO NOTHING;
            """, cancellationToken);
    }

    private static async Task WriteManifestAsync(string path, MigrationOperationManifest manifest, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        await using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken); await stream.FlushAsync(cancellationToken); stream.Flush(true);
    }

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder { DataSource = Path.GetFullPath(path), Mode = mode, ForeignKeys = true, Pooling = false, DefaultTimeout = 30 }.ToString());
}
