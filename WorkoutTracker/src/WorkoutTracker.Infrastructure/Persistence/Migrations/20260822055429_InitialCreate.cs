using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WorkoutTracker.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ThemeMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DashboardPeriod = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    DashboardExerciseIdsJson = table.Column<string>(type: "TEXT", maxLength: 4096, nullable: false),
                    WindowLeft = table.Column<double>(type: "REAL", nullable: true),
                    WindowTop = table.Column<double>(type: "REAL", nullable: true),
                    WindowWidth = table.Column<double>(type: "REAL", nullable: true),
                    WindowHeight = table.Column<double>(type: "REAL", nullable: true),
                    WindowState = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                    table.CheckConstraint("CK_AppSettings_Singleton", "Id = 1");
                });

            migrationBuilder.CreateTable(
                name: "MuscleGroups",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false, collation: "NOCASE"),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MuscleGroups", x => x.Id);
                    table.CheckConstraint("CK_MuscleGroups_DisplayOrder", "DisplayOrder >= 0");
                });

            migrationBuilder.CreateTable(
                name: "WorkoutMenus",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutMenus", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Exercises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    PrimaryMuscleId = table.Column<Guid>(type: "TEXT", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Exercises", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Exercises_MuscleGroups_PrimaryMuscleId",
                        column: x => x.PrimaryMuscleId,
                        principalTable: "MuscleGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutSessions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    SourceMenuId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    WorkoutDate = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 4000, nullable: true),
                    CompletedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutSessions", x => x.Id);
                    table.CheckConstraint("CK_WorkoutSessions_Status_CompletedAt", "(Status = 'Draft' AND CompletedAtUtcTicks IS NULL) OR (Status = 'Completed' AND CompletedAtUtcTicks IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_WorkoutSessions_WorkoutMenus_SourceMenuId",
                        column: x => x.SourceMenuId,
                        principalTable: "WorkoutMenus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ExerciseSecondaryMuscles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExerciseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    MuscleGroupId = table.Column<Guid>(type: "TEXT", nullable: false),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExerciseSecondaryMuscles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExerciseSecondaryMuscles_Exercises_ExerciseId",
                        column: x => x.ExerciseId,
                        principalTable: "Exercises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ExerciseSecondaryMuscles_MuscleGroups_MuscleGroupId",
                        column: x => x.MuscleGroupId,
                        principalTable: "MuscleGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MenuExercises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkoutMenuId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExerciseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    DefaultSetCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MenuExercises", x => x.Id);
                    table.CheckConstraint("CK_MenuExercises_DefaultSetCount", "DefaultSetCount BETWEEN 1 AND 20");
                    table.CheckConstraint("CK_MenuExercises_DisplayOrder", "DisplayOrder >= 0");
                    table.ForeignKey(
                        name: "FK_MenuExercises_Exercises_ExerciseId",
                        column: x => x.ExerciseId,
                        principalTable: "Exercises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MenuExercises_WorkoutMenus_WorkoutMenuId",
                        column: x => x.WorkoutMenuId,
                        principalTable: "WorkoutMenus",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutExercises",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkoutSessionId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExerciseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ExerciseNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    PrimaryMuscleIdSnapshot = table.Column<Guid>(type: "TEXT", nullable: false),
                    PrimaryMuscleNameSnapshot = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    DisplayOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    Note = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutExercises", x => x.Id);
                    table.CheckConstraint("CK_WorkoutExercises_DisplayOrder", "DisplayOrder >= 0");
                    table.ForeignKey(
                        name: "FK_WorkoutExercises_Exercises_ExerciseId",
                        column: x => x.ExerciseId,
                        principalTable: "Exercises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WorkoutExercises_WorkoutSessions_WorkoutSessionId",
                        column: x => x.WorkoutSessionId,
                        principalTable: "WorkoutSessions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WorkoutSets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    WorkoutExerciseId = table.Column<Guid>(type: "TEXT", nullable: false),
                    SetNumber = table.Column<int>(type: "INTEGER", nullable: false),
                    WeightHundredthsKg = table.Column<long>(type: "INTEGER", nullable: true),
                    Reps = table.Column<int>(type: "INTEGER", nullable: true),
                    Rpe = table.Column<decimal>(type: "NUMERIC", nullable: true),
                    CreatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false),
                    UpdatedAtUtcTicks = table.Column<long>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkoutSets", x => x.Id);
                    table.CheckConstraint("CK_WorkoutSets_Reps", "Reps IS NULL OR Reps BETWEEN 1 AND 1000");
                    table.CheckConstraint("CK_WorkoutSets_Rpe", "Rpe IS NULL OR (Rpe BETWEEN 1 AND 10 AND Rpe * 2 = CAST(Rpe * 2 AS INTEGER))");
                    table.CheckConstraint("CK_WorkoutSets_SetNumber", "SetNumber >= 1");
                    table.CheckConstraint("CK_WorkoutSets_Weight", "WeightHundredthsKg IS NULL OR WeightHundredthsKg BETWEEN 1 AND 100000");
                    table.ForeignKey(
                        name: "FK_WorkoutSets_WorkoutExercises_WorkoutExerciseId",
                        column: x => x.WorkoutExerciseId,
                        principalTable: "WorkoutExercises",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Exercises_IsActive_Name",
                table: "Exercises",
                columns: new[] { "IsActive", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Exercises_PrimaryMuscleId",
                table: "Exercises",
                column: "PrimaryMuscleId");

            migrationBuilder.CreateIndex(
                name: "UX_Exercises_Name_NoCase",
                table: "Exercises",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ExerciseSecondaryMuscles_MuscleGroupId",
                table: "ExerciseSecondaryMuscles",
                column: "MuscleGroupId");

            migrationBuilder.CreateIndex(
                name: "UX_ExerciseSecondaryMuscles_Exercise_Muscle",
                table: "ExerciseSecondaryMuscles",
                columns: new[] { "ExerciseId", "MuscleGroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MenuExercises_ExerciseId",
                table: "MenuExercises",
                column: "ExerciseId");

            migrationBuilder.CreateIndex(
                name: "UX_MenuExercises_Menu_Exercise",
                table: "MenuExercises",
                columns: new[] { "WorkoutMenuId", "ExerciseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_MenuExercises_Menu_Order",
                table: "MenuExercises",
                columns: new[] { "WorkoutMenuId", "DisplayOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MuscleGroups_DisplayOrder_Name",
                table: "MuscleGroups",
                columns: new[] { "DisplayOrder", "Name" });

            migrationBuilder.CreateIndex(
                name: "UX_MuscleGroups_Name_NoCase",
                table: "MuscleGroups",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutExercises_Exercise_Session",
                table: "WorkoutExercises",
                columns: new[] { "ExerciseId", "WorkoutSessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutExercises_PrimaryMuscle_Session",
                table: "WorkoutExercises",
                columns: new[] { "PrimaryMuscleIdSnapshot", "WorkoutSessionId" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkoutExercises_Session_Exercise",
                table: "WorkoutExercises",
                columns: new[] { "WorkoutSessionId", "ExerciseId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_WorkoutExercises_Session_Order",
                table: "WorkoutExercises",
                columns: new[] { "WorkoutSessionId", "DisplayOrder" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_History_Keyset",
                table: "WorkoutSessions",
                columns: new[] { "Status", "WorkoutDate", "CompletedAtUtcTicks", "Id" },
                descending: new[] { false, true, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_WorkoutSessions_SourceMenu_Date",
                table: "WorkoutSessions",
                columns: new[] { "SourceMenuId", "WorkoutDate" });

            migrationBuilder.CreateIndex(
                name: "UX_WorkoutSessions_OneDraft",
                table: "WorkoutSessions",
                column: "Status",
                unique: true,
                filter: "Status = 'Draft'");

            migrationBuilder.CreateIndex(
                name: "UX_WorkoutSets_Exercise_SetNumber",
                table: "WorkoutSets",
                columns: new[] { "WorkoutExerciseId", "SetNumber" },
                unique: true);

            migrationBuilder.Sql("PRAGMA user_version = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "ExerciseSecondaryMuscles");

            migrationBuilder.DropTable(
                name: "MenuExercises");

            migrationBuilder.DropTable(
                name: "WorkoutSets");

            migrationBuilder.DropTable(
                name: "WorkoutExercises");

            migrationBuilder.DropTable(
                name: "Exercises");

            migrationBuilder.DropTable(
                name: "WorkoutSessions");

            migrationBuilder.DropTable(
                name: "MuscleGroups");

            migrationBuilder.DropTable(
                name: "WorkoutMenus");

            migrationBuilder.Sql("PRAGMA user_version = 0;");
        }
    }
}
