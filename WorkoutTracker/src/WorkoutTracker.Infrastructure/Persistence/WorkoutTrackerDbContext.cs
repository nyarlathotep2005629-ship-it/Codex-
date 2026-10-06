using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

public sealed class WorkoutTrackerDbContext(DbContextOptions<WorkoutTrackerDbContext> options) : DbContext(options)
{
    public DbSet<MuscleGroup> MuscleGroups => Set<MuscleGroup>();
    public DbSet<Exercise> Exercises => Set<Exercise>();
    public DbSet<ExerciseSecondaryMuscle> ExerciseSecondaryMuscles => Set<ExerciseSecondaryMuscle>();
    public DbSet<WorkoutMenu> WorkoutMenus => Set<WorkoutMenu>();
    public DbSet<MenuExercise> MenuExercises => Set<MenuExercise>();
    public DbSet<WorkoutSession> WorkoutSessions => Set<WorkoutSession>();
    public DbSet<WorkoutExercise> WorkoutExercises => Set<WorkoutExercise>();
    public DbSet<WorkoutSet> WorkoutSets => Set<WorkoutSet>();
    public DbSet<AppSettings> AppSettings => Set<AppSettings>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) => ConfigureModel(modelBuilder);

    internal static void ConfigureModel(ModelBuilder b)
    {
        var ticks = new UtcTicksConverter();
        var nullableTicks = new NullableUtcTicksConverter();
        var dayNumber = new DayNumberConverter();
        var weight = new WeightHundredthsKgConverter();

        b.Entity<MuscleGroup>(e =>
        {
            e.ToTable("MuscleGroups", t => t.HasCheckConstraint("CK_MuscleGroups_DisplayOrder", "DisplayOrder >= 0"));
            e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(50).UseCollation("NOCASE").IsRequired();
            e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks);
            e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UX_MuscleGroups_Name_NoCase");
            e.HasIndex(x => new { x.DisplayOrder, x.Name }).HasDatabaseName("IX_MuscleGroups_DisplayOrder_Name");
        });
        b.Entity<Exercise>(e =>
        {
            e.ToTable("Exercises"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(100).UseCollation("NOCASE").IsRequired();
            e.Property(x => x.Note).HasMaxLength(2000); e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks);
            e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
            e.HasOne(x => x.PrimaryMuscle).WithMany().HasForeignKey(x => x.PrimaryMuscleId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Name).IsUnique().HasDatabaseName("UX_Exercises_Name_NoCase");
            e.HasIndex(x => new { x.IsActive, x.Name }).HasDatabaseName("IX_Exercises_IsActive_Name");
        });
        b.Entity<ExerciseSecondaryMuscle>(e =>
        {
            e.ToTable("ExerciseSecondaryMuscles"); e.HasKey(x => x.Id);
            e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks); e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
            e.HasOne(x => x.Exercise).WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.MuscleGroup).WithMany().HasForeignKey(x => x.MuscleGroupId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.ExerciseId, x.MuscleGroupId }).IsUnique().HasDatabaseName("UX_ExerciseSecondaryMuscles_Exercise_Muscle");
        });
        b.Entity<WorkoutMenu>(e =>
        {
            e.ToTable("WorkoutMenus"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(100).IsRequired(); e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks); e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
        });
        b.Entity<MenuExercise>(e =>
        {
            e.ToTable("MenuExercises", t => { t.HasCheckConstraint("CK_MenuExercises_DisplayOrder", "DisplayOrder >= 0"); t.HasCheckConstraint("CK_MenuExercises_DefaultSetCount", "DefaultSetCount BETWEEN 1 AND 20"); });
            e.HasKey(x => x.Id); e.Property(x => x.Note).HasMaxLength(1000); e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks); e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
            e.HasOne(x => x.WorkoutMenu).WithMany(x => x.Exercises).HasForeignKey(x => x.WorkoutMenuId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.WorkoutMenuId, x.ExerciseId }).IsUnique().HasDatabaseName("UX_MenuExercises_Menu_Exercise");
            e.HasIndex(x => new { x.WorkoutMenuId, x.DisplayOrder }).IsUnique().HasDatabaseName("UX_MenuExercises_Menu_Order");
        });
        b.Entity<WorkoutSession>(e =>
        {
            e.ToTable("WorkoutSessions", t => t.HasCheckConstraint("CK_WorkoutSessions_Status_CompletedAt", "(Status = 'Draft' AND CompletedAtUtcTicks IS NULL) OR (Status = 'Completed' AND CompletedAtUtcTicks IS NOT NULL)"));
            e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(100); e.Property(x => x.Note).HasMaxLength(4000);
            e.Property(x => x.WorkoutDate).HasConversion(dayNumber); e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.CompletedAtUtc).HasColumnName("CompletedAtUtcTicks").HasConversion(nullableTicks); e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks); e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
            e.HasOne(x => x.SourceMenu).WithMany().HasForeignKey(x => x.SourceMenuId).OnDelete(DeleteBehavior.SetNull);
            e.HasIndex(x => new { x.Status, x.WorkoutDate, x.CompletedAtUtc, x.Id }).IsDescending(false, true, true, true).HasDatabaseName("IX_WorkoutSessions_History_Keyset");
            e.HasIndex(x => new { x.SourceMenuId, x.WorkoutDate }).HasDatabaseName("IX_WorkoutSessions_SourceMenu_Date");
            e.HasIndex(x => x.Status).IsUnique().HasFilter("Status = 'Draft'").HasDatabaseName("UX_WorkoutSessions_OneDraft");
        });
        b.Entity<WorkoutExercise>(e =>
        {
            e.ToTable("WorkoutExercises", t => t.HasCheckConstraint("CK_WorkoutExercises_DisplayOrder", "DisplayOrder >= 0")); e.HasKey(x => x.Id);
            e.Property(x => x.ExerciseNameSnapshot).HasMaxLength(100).IsRequired(); e.Property(x => x.PrimaryMuscleNameSnapshot).HasMaxLength(50).IsRequired(); e.Property(x => x.Note).HasMaxLength(2000);
            e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks); e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
            e.HasOne(x => x.WorkoutSession).WithMany(x => x.Exercises).HasForeignKey(x => x.WorkoutSessionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(x => x.Exercise).WithMany().HasForeignKey(x => x.ExerciseId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.WorkoutSessionId, x.DisplayOrder }).IsUnique().HasDatabaseName("UX_WorkoutExercises_Session_Order");
            e.HasIndex(x => new { x.WorkoutSessionId, x.ExerciseId }).IsUnique().HasDatabaseName("UX_WorkoutExercises_Session_Exercise");
            e.HasIndex(x => new { x.ExerciseId, x.WorkoutSessionId }).HasDatabaseName("IX_WorkoutExercises_Exercise_Session");
            e.HasIndex(x => new { x.PrimaryMuscleIdSnapshot, x.WorkoutSessionId }).HasDatabaseName("IX_WorkoutExercises_PrimaryMuscle_Session");
        });
        b.Entity<WorkoutSet>(e =>
        {
            e.ToTable("WorkoutSets", t => { t.HasCheckConstraint("CK_WorkoutSets_SetNumber", "SetNumber >= 1"); t.HasCheckConstraint("CK_WorkoutSets_Weight", "WeightHundredthsKg IS NULL OR WeightHundredthsKg BETWEEN 1 AND 100000"); t.HasCheckConstraint("CK_WorkoutSets_Reps", "Reps IS NULL OR Reps BETWEEN 1 AND 1000"); t.HasCheckConstraint("CK_WorkoutSets_Rpe", "Rpe IS NULL OR (Rpe BETWEEN 1 AND 10 AND Rpe * 2 = CAST(Rpe * 2 AS INTEGER))"); });
            e.HasKey(x => x.Id); e.Property(x => x.WeightKg).HasColumnName("WeightHundredthsKg").HasConversion(weight); e.Property(x => x.Rpe).HasColumnType("NUMERIC");
            e.Property(x => x.CreatedAtUtc).HasColumnName("CreatedAtUtcTicks").HasConversion(ticks); e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
            e.HasOne(x => x.WorkoutExercise).WithMany(x => x.Sets).HasForeignKey(x => x.WorkoutExerciseId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.WorkoutExerciseId, x.SetNumber }).IsUnique().HasDatabaseName("UX_WorkoutSets_Exercise_SetNumber");
        });
        b.Entity<AppSettings>(e =>
        {
            e.ToTable("AppSettings", t => t.HasCheckConstraint("CK_AppSettings_Singleton", "Id = 1")); e.HasKey(x => x.Id);
            e.Property(x => x.ThemeMode).HasConversion<string>().HasMaxLength(16); e.Property(x => x.DashboardPeriod).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.DashboardExerciseIdsJson).HasMaxLength(4096); e.Property(x => x.WindowState).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.UpdatedAtUtc).HasColumnName("UpdatedAtUtcTicks").HasConversion(ticks);
        });
    }
}

