using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

internal sealed class AppSettingsStore(
    IDbContextFactory<WorkoutTrackerDbContext> factory,
    DatabaseLocation location) : IAppSettingsStore
{
    public async Task<AppSettingsDto> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var row = await db.AppSettings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        return new(row?.ThemeMode ?? ThemeMode.System, location.DatabasePath, location.BackupDirectory);
    }

    public async Task SaveThemeAsync(ThemeMode themeMode, DateTimeOffset updatedAtUtc, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO AppSettings
                (Id, ThemeMode, DashboardPeriod, DashboardExerciseIdsJson, WindowLeft, WindowTop, WindowWidth, WindowHeight, WindowState, UpdatedAtUtcTicks)
            VALUES
                (1, {themeMode.ToString()}, 'ThreeMonths', '[]', NULL, NULL, NULL, NULL, 'Normal', {updatedAtUtc.UtcTicks})
            ON CONFLICT(Id) DO UPDATE SET
                ThemeMode = excluded.ThemeMode,
                UpdatedAtUtcTicks = excluded.UpdatedAtUtcTicks;
            """, cancellationToken);
    }
}

public sealed record DatabaseLocation(string DatabasePath)
{
    public string BackupDirectory => Path.Combine(Path.GetDirectoryName(DatabasePath)!, "Backups");
}

internal sealed class BackupInfrastructure(
    DatabaseLocation location,
    ForeignKeyVerificationInterceptor connectionTracker) : IBackupInfrastructure
{
    private static readonly string[] RequiredTables =
    [
        "AppSettings", "MuscleGroups", "Exercises", "ExerciseSecondaryMuscles", "WorkoutMenus",
        "MenuExercises", "WorkoutSessions", "WorkoutExercises", "WorkoutSets", "__EFMigrationsHistory"
    ];

    public async Task WaitForConnectionsAndCheckpointAsync(CancellationToken cancellationToken)
    {
        await connectionTracker.WaitForZeroAsync(cancellationToken);
        SqliteConnection.ClearAllPools();

        await Task.Run(async () =>
        {
            await using var connection = CreateConnection(location.DatabasePath, SqliteOpenMode.ReadWrite, false);
            await connection.OpenAsync(cancellationToken);
            await using (var checkpoint = connection.CreateCommand())
            {
                checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await using var reader = await checkpoint.ExecuteReaderAsync(cancellationToken);
                if (!await reader.ReadAsync(cancellationToken) || reader.GetInt32(0) != 0)
                    throw new InvalidOperationException("WAL checkpoint is busy.");
            }
            await EnsureQuickCheckAsync(connection, cancellationToken);
        }, cancellationToken);

        SqliteConnection.ClearAllPools();
    }

    public async Task<BackupResult> CreateAndValidateBackupAsync(CancellationToken cancellationToken)
    {
        return await Task.Run(async () =>
        {
            Directory.CreateDirectory(location.BackupDirectory);
            var destination = GetAvailableBackupPath(location.BackupDirectory, DateTime.Now);

            await using (var source = CreateConnection(location.DatabasePath, SqliteOpenMode.ReadOnly, false))
            await using (var backup = CreateConnection(destination, SqliteOpenMode.ReadWriteCreate, false))
            {
                await source.OpenAsync(cancellationToken);
                await backup.OpenAsync(cancellationToken);
                source.BackupDatabase(backup);
            }

            SqliteConnection.ClearAllPools();
            await EnsureSqliteHeaderAsync(destination, cancellationToken);
            await using var validation = CreateConnection(destination, SqliteOpenMode.ReadOnly, false);
            await validation.OpenAsync(cancellationToken);
            await EnsureQuickCheckAsync(validation, cancellationToken);

            var schemaVersion = await ScalarIntAsync(validation, "PRAGMA user_version;", cancellationToken);
            if (schemaVersion != 1) throw new InvalidOperationException($"Unsupported schema version: {schemaVersion}.");
            foreach (var table in RequiredTables)
            {
                await using var command = validation.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
                command.Parameters.AddWithValue("$name", table);
                if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
                    throw new InvalidOperationException($"Backup is missing required table '{table}'.");
            }
            var historyCount = await ScalarIntAsync(validation, "SELECT COUNT(*) FROM __EFMigrationsHistory;", cancellationToken);
            if (historyCount == 0) throw new InvalidOperationException("Backup has no EF migration history.");
            return new BackupResult(destination, schemaVersion, RequiredTables, true, true);
        }, cancellationToken);
    }

    public async Task VerifyPrimaryDatabaseAsync(CancellationToken cancellationToken)
    {
        SqliteConnection.ClearAllPools();
        await Task.Run(async () =>
        {
            await using var connection = CreateConnection(location.DatabasePath, SqliteOpenMode.ReadOnly, false);
            await connection.OpenAsync(cancellationToken);
            await EnsureQuickCheckAsync(connection, cancellationToken);
        }, cancellationToken);
    }

    private static SqliteConnection CreateConnection(string path, SqliteOpenMode mode, bool pooling) => new(new SqliteConnectionStringBuilder
    {
        DataSource = Path.GetFullPath(path), Mode = mode, ForeignKeys = true, Pooling = pooling
    }.ToString());

    private static async Task EnsureQuickCheckAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        if (!string.Equals(Convert.ToString(await command.ExecuteScalarAsync(cancellationToken)), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("SQLite quick_check failed.");
    }

    private static async Task EnsureSqliteHeaderAsync(string path, CancellationToken cancellationToken)
    {
        var expected = "SQLite format 3\0"u8.ToArray();
        var actual = new byte[expected.Length];
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (await stream.ReadAsync(actual, cancellationToken) != expected.Length || !actual.SequenceEqual(expected))
            throw new InvalidOperationException("Backup does not have a valid SQLite header.");
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string GetAvailableBackupPath(string directory, DateTime localNow)
    {
        var stem = $"WorkoutTracker_Backup_{localNow:yyyyMMdd_HHmmss}";
        var candidate = Path.Combine(directory, stem + ".db");
        for (var suffix = 1; File.Exists(candidate); suffix++) candidate = Path.Combine(directory, $"{stem}_{suffix:00}.db");
        return candidate;
    }
}
