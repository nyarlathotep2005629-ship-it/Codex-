using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class RestoreTests
{
    [Fact(DisplayName = "BKP-005 BKP-009 BKP-010 BKP-019 BKP-020 BKP-022 Restore replaces primary, retains Emergency, reloads State, and resumes")]
    public async Task RestoreSuccess()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.MigrateAsync();
        await fixture.Settings.SaveThemeAsync(ThemeMode.Light);
        var started = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, new DateOnly(2026, 8, 22))));
        var restoredDraft = started.Workout with { WorkoutName = "draft-in-selected-backup" };
        fixture.AutoSave.NotifyChanged(restoredDraft, new(restoredDraft.SessionId, restoredDraft.WorkoutDate, restoredDraft.WorkoutName, null, []), true);
        await fixture.AutoSave.FlushAsync();
        var selected = (await fixture.Backup.CreateBackupAsync()).BackupPath;
        var selectedHash = Hash(selected);

        await fixture.ActiveWorkout.ClearAndDiscardForTestAsync(fixture.VerticalSlice);
        await fixture.Settings.SaveThemeAsync(ThemeMode.Dark);
        var result = await fixture.Restore.RestoreAsync(selected, true);

        Assert.True(result.Succeeded); Assert.False(result.RolledBack);
        Assert.True(File.Exists(result.EmergencyBackupPath)); Assert.Equal(selectedHash, Hash(selected));
        Assert.Equal(ThemeMode.Light, (await fixture.Settings.GetAsync()).ThemeMode);
        Assert.True(fixture.ActiveWorkout.IsActive); Assert.Equal("draft-in-selected-backup", fixture.ActiveWorkout.CurrentWorkout!.WorkoutName);
        Assert.Equal(MaintenanceStage.Off, fixture.WriteGate.Status.Stage);
        await fixture.Settings.SaveThemeAsync(ThemeMode.System);
        var maintenance = Path.Combine(fixture.Directory, "Maintenance");
        Assert.Empty(Directory.EnumerateFiles(maintenance, $"WorkoutTracker_Restore_{result.OperationId}.*"));
    }

    [Theory(DisplayName = "BKP-013 Restore preflight rejects invalid / incompatible source without changing Primary")]
    [InlineData("header")]
    [InlineData("schema")]
    [InlineData("migration")]
    [InlineData("table")]
    [InlineData("corrupt")]
    public async Task RestorePreflightRejects(string fault)
    {
        await using var fixture = new DatabaseTestFixture(); await fixture.MigrateAsync(); await fixture.Settings.SaveThemeAsync(ThemeMode.Dark);
        var valid = (await fixture.Backup.CreateBackupAsync()).BackupPath;
        var candidate = Path.Combine(fixture.Directory, $"invalid-{fault}.db"); File.Copy(valid, candidate);
        switch (fault)
        {
            case "header": await File.WriteAllTextAsync(candidate, "not sqlite"); break;
            case "schema": await ExecuteAsync(candidate, "PRAGMA user_version=99;"); break;
            case "migration": await ExecuteAsync(candidate, "DELETE FROM __EFMigrationsHistory;"); break;
            case "table": await ExecuteAsync(candidate, "DROP TABLE WorkoutSets;"); break;
            case "corrupt": CorruptPage(candidate); break;
        }
        var primaryHash = Hash(fixture.DatabasePath);
        await Assert.ThrowsAnyAsync<Exception>(() => fixture.Restore.ValidateAsync(candidate));
        Assert.Equal(primaryHash, Hash(fixture.DatabasePath)); Assert.Equal(MaintenanceStage.Off, fixture.WriteGate.Status.Stage);
    }

    [Theory(DisplayName = "BKP-011 BKP-021 Failure before replacement leaves Primary unchanged")]
    [InlineData(RestoreFailurePoint.CreateEmergencyBackup)]
    [InlineData(RestoreFailurePoint.CreateStagedDatabase)]
    [InlineData(RestoreFailurePoint.BeforeReplace)]
    public async Task FailureBeforeReplaceIsSafe(RestoreFailurePoint point)
    {
        var injector = new MutableRestoreFailureInjector(point);
        await using var fixture = new DatabaseTestFixture(services => services.AddSingleton<IRestoreFailureInjector>(injector));
        await fixture.MigrateAsync(); await fixture.Settings.SaveThemeAsync(ThemeMode.Light); var selected = (await fixture.Backup.CreateBackupAsync()).BackupPath;
        await fixture.Settings.SaveThemeAsync(ThemeMode.Dark);
        var error = await Assert.ThrowsAsync<RestoreOperationException>(() => fixture.Restore.RestoreAsync(selected, true));
        Assert.True(error.PrimaryDatabaseRestored); Assert.False(error.IsFatal);
        Assert.Equal(ThemeMode.Dark, (await fixture.Settings.GetAsync()).ThemeMode); Assert.Equal(MaintenanceStage.Off, fixture.WriteGate.Status.Stage);
    }

    [Theory(DisplayName = "BKP-011 BKP-021 Failure after replacement rolls back to original Primary")]
    [InlineData(RestoreFailurePoint.AfterReplace)]
    [InlineData(RestoreFailurePoint.ValidateRestoredDatabase)]
    public async Task FailureAfterReplaceRollsBack(RestoreFailurePoint point)
    {
        var injector = new MutableRestoreFailureInjector(point);
        await using var fixture = new DatabaseTestFixture(services => services.AddSingleton<IRestoreFailureInjector>(injector));
        await fixture.MigrateAsync(); await fixture.Settings.SaveThemeAsync(ThemeMode.Light); var selected = (await fixture.Backup.CreateBackupAsync()).BackupPath;
        await fixture.Settings.SaveThemeAsync(ThemeMode.Dark); var originalHash = Hash(fixture.DatabasePath);
        var error = await Assert.ThrowsAsync<RestoreOperationException>(() => fixture.Restore.RestoreAsync(selected, true));
        Assert.True(error.PrimaryDatabaseRestored); Assert.False(error.IsFatal); Assert.Equal(originalHash, Hash(fixture.DatabasePath));
        Assert.Equal(ThemeMode.Dark, (await fixture.Settings.GetAsync()).ThemeMode); Assert.Equal(MaintenanceStage.Off, fixture.WriteGate.Status.Stage);
        Assert.True(File.Exists(error.EmergencyBackupPath));
    }

    [Fact(DisplayName = "BKP-021 Rollback failure keeps Fatal Maintenance and exposes recovery files")]
    public async Task RollbackFailureIsFatal()
    {
        var injector = new MutableRestoreFailureInjector(RestoreFailurePoint.AfterReplace, RestoreFailurePoint.Rollback);
        await using var fixture = new DatabaseTestFixture(services => services.AddSingleton<IRestoreFailureInjector>(injector));
        await fixture.MigrateAsync(); await fixture.Settings.SaveThemeAsync(ThemeMode.Light); var selected = (await fixture.Backup.CreateBackupAsync()).BackupPath;
        await fixture.Settings.SaveThemeAsync(ThemeMode.Dark);
        var error = await Assert.ThrowsAsync<RestoreOperationException>(() => fixture.Restore.RestoreAsync(selected, true));
        Assert.True(error.IsFatal); Assert.False(error.PrimaryDatabaseRestored); Assert.Equal(MaintenanceStage.Fatal, fixture.WriteGate.Status.Stage);
        Assert.True(File.Exists(error.EmergencyBackupPath)); Assert.NotNull(error.FailedDatabasePath);
        Assert.Throws<MaintenanceModeException>(() => fixture.WriteGate.EnterPublicWrite());
    }

    [Fact(DisplayName = "BKP-013 missing Restore source is rejected before Maintenance")]
    public async Task MissingSourceRejected()
    {
        await using var fixture = new DatabaseTestFixture(); await fixture.MigrateAsync();
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.Restore.ValidateAsync(Path.Combine(fixture.Directory, "missing.db")));
        Assert.Equal(MaintenanceStage.Off, fixture.WriteGate.Status.Stage);
    }

    private static byte[] Hash(string path) { SqliteConnection.ClearAllPools(); return SHA256.HashData(File.ReadAllBytes(path)); }
    private static async Task ExecuteAsync(string path, string sql)
    {
        SqliteConnection.ClearAllPools(); await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static void CorruptPage(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None); stream.SetLength(100); stream.Flush(true);
    }

    private sealed class MutableRestoreFailureInjector(params RestoreFailurePoint[] points) : IRestoreFailureInjector
    {
        private readonly HashSet<RestoreFailurePoint> _points = [.. points];
        public void ThrowIfRequested(RestoreFailurePoint point) { if (_points.Contains(point)) throw new IOException($"Injected {point}"); }
    }
}

internal static class RestoreTestExtensions
{
    public static async Task ClearAndDiscardForTestAsync(this ActiveWorkoutState state, IWorkoutVerticalSlicePersistence persistence)
    {
        if (state.SessionId is { } id) await persistence.DiscardAsync(id); state.Clear();
    }
}
