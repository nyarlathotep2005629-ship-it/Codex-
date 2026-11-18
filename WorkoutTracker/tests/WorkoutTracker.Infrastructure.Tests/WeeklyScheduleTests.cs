using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class WeeklyScheduleTests
{
    [Fact]
    public async Task BaseWeekRepeatsAndOverridesStayWithinTheirScope()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var menuId = Guid.NewGuid();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.WorkoutMenus.Add(new WorkoutMenu(menuId, "脚の日", null, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var monday = new DateOnly(2026, 11, 2);
        await fixture.WeeklySchedule.SetBaseDayAsync(DayOfWeek.Monday, menuId, false);
        await fixture.WeeklySchedule.SetBaseDayAsync(DayOfWeek.Tuesday, null, true);
        await fixture.WeeklySchedule.SetWeekDayAsync(monday, null, true);
        await fixture.WeeklySchedule.SetDateAsync(monday, menuId, false);

        var thisWeek = await fixture.WeeklySchedule.GetWeekAsync(monday);
        Assert.Equal(ScheduleSource.SelectedDate, thisWeek[0].Source);
        Assert.Equal(menuId, thisWeek[0].MenuId);
        Assert.True(thisWeek[1].IsRest);
        var nextWeek = await fixture.WeeklySchedule.GetWeekAsync(monday.AddDays(7));
        Assert.Equal(ScheduleSource.BaseWeek, nextWeek[0].Source);
        Assert.Equal(menuId, nextWeek[0].MenuId);
        await fixture.WeeklySchedule.ClearDateAsync(monday);
        Assert.True((await fixture.WeeklySchedule.GetWeekAsync(monday))[0].IsRest);
        await fixture.WeeklySchedule.ClearWeekDayAsync(monday);
        Assert.Equal(menuId, (await fixture.WeeklySchedule.GetWeekAsync(monday))[0].MenuId);
    }

    [Fact]
    public async Task ExistingVersionOneDatabaseUpgradesWithoutChangingMenuOrHistory()
    {
        await using var fixture = new DatabaseTestFixture();
        var menuId = Guid.NewGuid();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            await db.Database.MigrateAsync("20260822055429_InitialCreate");
            db.WorkoutMenus.Add(new WorkoutMenu(menuId, "既存メニュー", null, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal("既存メニュー", (await verify.WorkoutMenus.SingleAsync()).Name);
        Assert.Equal(menuId, (await verify.WorkoutMenus.SingleAsync()).Id);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.DatabasePath, Mode = SqliteOpenMode.ReadOnly }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = "PRAGMA user_version;";
        Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync()));
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Directory, "Backups", "Migration"), "*.db"));
    }

    [Fact]
    public async Task ScheduleTransfersWithMenuAndSurvivesReimport()
    {
        await using var source = new DatabaseTestFixture();
        await source.Initializer.InitializeFreshDatabaseAsync();
        var menuId = Guid.NewGuid();
        await using (var db = await source.Factory.CreateDbContextAsync())
        {
            db.WorkoutMenus.Add(new WorkoutMenu(menuId, "胸の日", null, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        await source.WeeklySchedule.SetBaseDayAsync(DayOfWeek.Monday, menuId, false);
        var path = Path.Combine(source.Directory, "weekly.wtb");
        await source.Transfer.ExportAsync(path, "Windows", "test");

        await using var target = new DatabaseTestFixture();
        await target.Initializer.InitializeFreshDatabaseAsync();
        var plan = await target.Transfer.CreateImportPlanAsync(path);
        Assert.True(plan.CanCommit);
        await target.Transfer.ImportAsync(plan, []);
        Assert.Equal(menuId, (await target.WeeklySchedule.GetWeekAsync(new DateOnly(2026, 11, 2)))[0].MenuId);
        var repeat = await target.Transfer.CreateImportPlanAsync(path);
        Assert.True(repeat.CanCommit);
        await target.Transfer.ImportAsync(repeat, []);
        Assert.Equal(menuId, (await target.WeeklySchedule.GetWeekAsync(new DateOnly(2026, 11, 9)))[0].MenuId);
    }
}
