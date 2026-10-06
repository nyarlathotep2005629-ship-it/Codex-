using Microsoft.Data.Sqlite;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class SettingsBackupTests
{
    [Fact(DisplayName = "THM-003 Theme setting persists across service reads")]
    public async Task Thm003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.MigrateAsync();
        await fixture.Settings.SaveThemeAsync(ThemeMode.Dark);
        Assert.Equal(ThemeMode.Dark, (await fixture.Settings.GetAsync()).ThemeMode);
        await fixture.Settings.SaveThemeAsync(ThemeMode.System);
        Assert.Equal(ThemeMode.System, (await fixture.Settings.GetAsync()).ThemeMode);
    }

    [Fact(DisplayName = "BKP-001-003 BackupDatabase output opens read-only with valid schema and quick_check")]
    public async Task Bkp001To003()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.MigrateAsync();
        await fixture.Settings.SaveThemeAsync(ThemeMode.Dark);

        var result = await fixture.Backup.CreateBackupAsync();

        Assert.Matches(@"WorkoutTracker_Backup_\d{8}_\d{6}(_\d{2})?\.db$", result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.True(result.QuickCheckPassed); Assert.Equal(1, result.SchemaVersion);
        Assert.Contains("__EFMigrationsHistory", result.RequiredTables);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = result.BackupPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var check = connection.CreateCommand(); check.CommandText = "PRAGMA quick_check;";
        Assert.Equal("ok", Convert.ToString(await check.ExecuteScalarAsync()));
        await using var setting = connection.CreateCommand(); setting.CommandText = "SELECT ThemeMode FROM AppSettings WHERE Id=1;";
        Assert.Equal("Dark", Convert.ToString(await setting.ExecuteScalarAsync()));
    }

    [Fact(DisplayName = "BKP-004 BKP-016 Backup flushes pending latest Revision and preserves Active Workout")]
    public async Task Bkp004Bkp016()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.MigrateAsync();
        var started = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, new DateOnly(2026, 8, 22))));
        var latest = started.Workout with { WorkoutName = "latest-before-backup" };
        fixture.AutoSave.NotifyChanged(latest, new(latest.SessionId, latest.WorkoutDate, latest.WorkoutName, latest.WorkoutNote, []), false);

        var result = await fixture.Backup.CreateBackupAsync();

        Assert.True(fixture.ActiveWorkout.IsActive);
        Assert.Equal(fixture.ActiveWorkout.EditRevision, fixture.ActiveWorkout.LastSavedRevision);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = result.BackupPath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name FROM WorkoutSessions WHERE Status='Draft';";
        Assert.Equal("latest-before-backup", Convert.ToString(await command.ExecuteScalarAsync()));
    }

    [Fact(DisplayName = "BKP-008 BKP-012 BKP-018 checkpoint clears WAL safely and normal writes resume")]
    public async Task Bkp008Bkp012Bkp018()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.MigrateAsync();
        for (var i = 0; i < 3; i++)
        {
            await fixture.Settings.SaveThemeAsync(i % 2 == 0 ? ThemeMode.Light : ThemeMode.Dark);
            var result = await fixture.Backup.CreateBackupAsync();
            Assert.True(result.WalCheckpointPassed);
            var wal = fixture.DatabasePath + "-wal";
            Assert.True(!File.Exists(wal) || new FileInfo(wal).Length == 0);
        }
        await fixture.Settings.SaveThemeAsync(ThemeMode.System);
        Assert.Equal(ThemeMode.System, (await fixture.Settings.GetAsync()).ThemeMode);
    }
}
