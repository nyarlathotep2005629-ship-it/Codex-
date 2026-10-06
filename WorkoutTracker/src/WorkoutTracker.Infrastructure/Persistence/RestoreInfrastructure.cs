using Microsoft.Data.Sqlite;
using WorkoutTracker.Application;

namespace WorkoutTracker.Infrastructure.Persistence;

internal sealed class RestoreInfrastructure(
    DatabaseLocation location,
    IRestoreFailureInjector failureInjector) : IRestoreInfrastructure
{
    internal const int CurrentSchemaVersion = 1;
    internal const string CurrentMigrationId = "20260822055429_InitialCreate";
    private static readonly string[] RequiredTables =
    [
        "AppSettings", "MuscleGroups", "Exercises", "ExerciseSecondaryMuscles", "WorkoutMenus",
        "MenuExercises", "WorkoutSessions", "WorkoutExercises", "WorkoutSets", "__EFMigrationsHistory"
    ];

    public Task<DatabaseValidationResult> ValidateCandidateAsync(string selectedBackupPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selectedBackupPath);
        failureInjector.ThrowIfRequested(RestoreFailurePoint.ReadSelectedBackup);
        return Task.Run(() => ValidateDatabaseAsync(Path.GetFullPath(selectedBackupPath), requireForeignKeys: false, cancellationToken), cancellationToken);
    }

    public async Task<RestoreWorkFiles> PrepareAsync(string selectedBackupPath, Guid operationId, CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            var dbDirectory = Path.GetDirectoryName(location.DatabasePath)!;
            var maintenance = Path.Combine(dbDirectory, "Maintenance");
            var emergencyDirectory = Path.Combine(dbDirectory, "Backups", "Emergency");
            Directory.CreateDirectory(maintenance); Directory.CreateDirectory(emergencyDirectory);
            var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var files = new RestoreWorkFiles(
                operationId,
                Path.GetFullPath(selectedBackupPath),
                Path.Combine(emergencyDirectory, $"WorkoutTracker_Emergency_{stamp}_{operationId}.db"),
                Path.Combine(maintenance, $"WorkoutTracker_Restore_{operationId}.staged.db"),
                Path.Combine(maintenance, $"WorkoutTracker_Restore_{operationId}.swap-rollback.db"),
                Path.Combine(maintenance, $"WorkoutTracker_Restore_{operationId}.failed.db"),
                Path.Combine(maintenance, $"WorkoutTracker_{operationId}.wal-quarantine"),
                Path.Combine(maintenance, $"WorkoutTracker_{operationId}.shm-quarantine"));

            EnsureSameVolume(location.DatabasePath, files.StagedPath);
            failureInjector.ThrowIfRequested(RestoreFailurePoint.CreateEmergencyBackup);
            await BackupDatabaseAsync(location.DatabasePath, files.EmergencyBackupPath, cancellationToken);
            await ValidateDatabaseAsync(files.EmergencyBackupPath, requireForeignKeys: false, cancellationToken);
            await FinalizeStandaloneDatabaseAsync(files.EmergencyBackupPath, cancellationToken);

            failureInjector.ThrowIfRequested(RestoreFailurePoint.CreateStagedDatabase);
            await BackupDatabaseAsync(files.SelectedBackupPath, files.StagedPath, cancellationToken);
            await ValidateDatabaseAsync(files.StagedPath, requireForeignKeys: false, cancellationToken);
            await FinalizeStandaloneDatabaseAsync(files.StagedPath, cancellationToken);
            return files;
        }, cancellationToken);
    }

    public Task ReplacePrimaryAsync(RestoreWorkFiles files, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        failureInjector.ThrowIfRequested(RestoreFailurePoint.BeforeReplace);
        SqliteConnection.ClearAllPools();
        QuarantineSidecar(location.DatabasePath + "-wal", files.WalQuarantinePath);
        QuarantineSidecar(location.DatabasePath + "-shm", files.ShmQuarantinePath);
        if (File.Exists(location.DatabasePath + "-wal") || File.Exists(location.DatabasePath + "-shm"))
            throw new IOException("SQLite sidecar was regenerated before Restore replacement.");

        try
        {
            File.Replace(files.StagedPath, location.DatabasePath, files.SwapRollbackPath, true);
        }
        catch (PlatformNotSupportedException)
        {
            File.Move(location.DatabasePath, files.SwapRollbackPath);
            try { File.Move(files.StagedPath, location.DatabasePath); }
            catch { if (!File.Exists(location.DatabasePath) && File.Exists(files.SwapRollbackPath)) File.Move(files.SwapRollbackPath, location.DatabasePath); throw; }
        }
        failureInjector.ThrowIfRequested(RestoreFailurePoint.AfterReplace);
    }, cancellationToken);

    public Task<DatabaseValidationResult> ValidatePrimaryAfterRestoreAsync(CancellationToken cancellationToken)
    {
        failureInjector.ThrowIfRequested(RestoreFailurePoint.ValidateRestoredDatabase);
        return Task.Run(() => ValidateDatabaseAsync(location.DatabasePath, requireForeignKeys: true, cancellationToken), cancellationToken);
    }

    public Task<DatabaseValidationResult> RollbackAsync(RestoreWorkFiles files, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        failureInjector.ThrowIfRequested(RestoreFailurePoint.Rollback);
        SqliteConnection.ClearAllPools();
        Exception? swapError = null;
        if (File.Exists(files.SwapRollbackPath))
        {
            try
            {
                PreserveFailedPrimary(files.FailedPath);
                File.Move(files.SwapRollbackPath, location.DatabasePath);
                SqliteConnection.ClearAllPools();
                return await ValidateDatabaseAsync(location.DatabasePath, requireForeignKeys: true, cancellationToken);
            }
            catch (Exception ex) { swapError = ex; }
        }

        try
        {
            PreserveFailedPrimary(files.FailedPath);
            var recoveryStage = files.StagedPath + ".recovery";
            if (File.Exists(recoveryStage)) File.Delete(recoveryStage);
            await BackupDatabaseAsync(files.EmergencyBackupPath, recoveryStage, cancellationToken);
            await ValidateDatabaseAsync(recoveryStage, requireForeignKeys: false, cancellationToken);
            File.Move(recoveryStage, location.DatabasePath);
            SqliteConnection.ClearAllPools();
            return await ValidateDatabaseAsync(location.DatabasePath, requireForeignKeys: true, cancellationToken);
        }
        catch (Exception emergencyError)
        {
            throw new AggregateException("Swap rollback and Emergency Backup recovery both failed.", swapError ?? new InvalidOperationException("Swap rollback unavailable."), emergencyError);
        }
    }, cancellationToken);

    public Task CleanupSuccessfulRestoreAsync(RestoreWorkFiles files, CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeleteIfExists(files.StagedPath);
        DeleteIfExists(files.StagedPath + "-wal");
        DeleteIfExists(files.StagedPath + "-shm");
        DeleteIfExists(files.SwapRollbackPath);
        DeleteIfExists(files.SwapRollbackPath + "-wal");
        DeleteIfExists(files.SwapRollbackPath + "-shm");
        DeleteIfExists(files.WalQuarantinePath);
        DeleteIfExists(files.ShmQuarantinePath);
    }, cancellationToken);

    private void PreserveFailedPrimary(string failedPath)
    {
        if (!File.Exists(location.DatabasePath)) return;
        var target = failedPath;
        for (var suffix = 1; File.Exists(target); suffix++) target = Path.ChangeExtension(failedPath, $"{suffix:00}.db");
        File.Move(location.DatabasePath, target);
    }

    private static async Task BackupDatabaseAsync(string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        await using var source = CreateConnection(sourcePath, SqliteOpenMode.ReadOnly);
        await using var destination = CreateConnection(destinationPath, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(cancellationToken); await destination.OpenAsync(cancellationToken);
        source.BackupDatabase(destination);
    }

    private static async Task FinalizeStandaloneDatabaseAsync(string path, CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        await using (var connection = CreateConnection(path, SqliteOpenMode.ReadWrite))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 0) throw new InvalidOperationException("Standalone database WAL checkpoint is busy.");
        }
        SqliteConnection.ClearAllPools();
        DeleteIfExists(path + "-wal"); DeleteIfExists(path + "-shm");
    }

    private static async Task<DatabaseValidationResult> ValidateDatabaseAsync(string path, bool requireForeignKeys, CancellationToken cancellationToken)
    {
        await EnsureHeaderAsync(path, cancellationToken);
        await using var connection = CreateConnection(path, SqliteOpenMode.ReadOnly);
        await connection.OpenAsync(cancellationToken);
        if (requireForeignKeys && await ScalarLongAsync(connection, "PRAGMA foreign_keys;", cancellationToken) != 1)
            throw new InvalidOperationException("SQLite foreign key enforcement is disabled.");
        await using (var quick = connection.CreateCommand())
        {
            quick.CommandText = "PRAGMA quick_check;";
            if (!string.Equals(Convert.ToString(await quick.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite quick_check failed.");
        }
        var version = checked((int)await ScalarLongAsync(connection, "PRAGMA user_version;", cancellationToken));
        if (version != CurrentSchemaVersion) throw new InvalidOperationException($"Unsupported schema version: {version}.");
        foreach (var table in RequiredTables)
        {
            await using var tableCommand = connection.CreateCommand();
            tableCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            tableCommand.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(await tableCommand.ExecuteScalarAsync(cancellationToken)) != 1)
                throw new InvalidOperationException($"Required table is missing: {table}.");
        }
        var migrations = new List<string>();
        await using (var history = connection.CreateCommand())
        {
            history.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId;";
            await using var reader = await history.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) migrations.Add(reader.GetString(0));
        }
        if (migrations.Count != 1 || migrations[0] != CurrentMigrationId)
            throw new InvalidOperationException("EF Migration history is incompatible with this application version.");
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in new[] { "MuscleGroups", "Exercises", "WorkoutMenus", "WorkoutSessions", "WorkoutExercises", "WorkoutSets" })
            counts[table] = await ScalarLongAsync(connection, $"SELECT COUNT(*) FROM {table};", cancellationToken);
        return new(path, version, migrations, counts);
    }

    private static async Task EnsureHeaderAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Restore source was not found.", path);
        var expected = "SQLite format 3\0"u8.ToArray(); var actual = new byte[expected.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (await stream.ReadAsync(actual, cancellationToken) != expected.Length || !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Selected file is not a SQLite database.");
    }

    private static async Task<long> ScalarLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(path), Mode = mode, ForeignKeys = true, Pooling = false, DefaultTimeout = 30
    }.ToString());

    private static void EnsureSameVolume(string current, string staged)
    {
        if (!string.Equals(Path.GetPathRoot(Path.GetFullPath(current)), Path.GetPathRoot(Path.GetFullPath(staged)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restore staging must be on the same volume as the primary database.");
    }

    private static void QuarantineSidecar(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination);
    }

    private static void DeleteIfExists(string path) { if (File.Exists(path)) File.Delete(path); }
}
