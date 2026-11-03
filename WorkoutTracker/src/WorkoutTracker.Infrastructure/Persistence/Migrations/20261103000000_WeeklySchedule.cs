using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace WorkoutTracker.Infrastructure.Persistence.Migrations;

[DbContext(typeof(WorkoutTrackerDbContext))]
[Migration("20261103000000_WeeklySchedule")]
public sealed class WeeklySchedule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE WeeklyScheduleRules (
                Kind INTEGER NOT NULL CHECK (Kind BETWEEN 0 AND 2),
                AnchorDayNumber INTEGER NOT NULL CHECK (AnchorDayNumber >= 0),
                Weekday INTEGER NOT NULL CHECK (Weekday BETWEEN 0 AND 6),
                MenuId TEXT NULL REFERENCES WorkoutMenus(Id) ON DELETE SET NULL,
                IsRest INTEGER NOT NULL CHECK (IsRest IN (0, 1)),
                PRIMARY KEY (Kind, AnchorDayNumber, Weekday),
                CHECK (MenuId IS NULL OR IsRest = 0)
            );
            CREATE INDEX IX_WeeklyScheduleRules_MenuId ON WeeklyScheduleRules(MenuId);
            PRAGMA user_version = 2;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE WeeklyScheduleRules;
            PRAGMA user_version = 1;
            """);
    }
}
