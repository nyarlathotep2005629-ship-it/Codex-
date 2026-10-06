using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class LifecycleTests
{
    [Fact(DisplayName = "DB-010 MIG-001 Fresh Install and repeated Existing DB Startup are idempotent")]
    public async Task FreshAndExistingDatabaseStartup()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        await fixture.Settings.SaveThemeAsync(ThemeMode.Dark);

        await fixture.Initializer.InitializeFreshDatabaseAsync();

        Assert.Equal(ThemeMode.Dark, (await fixture.Settings.GetAsync()).ThemeMode);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Single(await db.Database.GetAppliedMigrationsAsync());
        Assert.Single(await db.AppSettings.ToListAsync());
    }

    [Fact(DisplayName = "ACT-003 ACT-004 ACT-005 ACT-008 abnormal exit next startup detects and rehydrates committed Draft baseline")]
    public async Task AbnormalExitNextStartupRehydratesDraft()
    {
        await using var fixture = new DatabaseTestFixture(); await fixture.MigrateAsync();
        var started = Assert.IsType<StartWorkoutResult.Started>(await fixture.StartWorkout.ExecuteAsync(new(null, new DateOnly(2026, 8, 22))));
        var latest = started.Workout with { WorkoutName = "persisted-before-process-exit", WorkoutNote = "safe draft" };
        fixture.AutoSave.NotifyChanged(latest, new(latest.SessionId, latest.WorkoutDate, latest.WorkoutName, latest.WorkoutNote, []), true);
        await fixture.AutoSave.FlushAsync();

        fixture.ActiveWorkout.Clear();
        fixture.AutoSave.BeginWorkout();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var useCase = new LoadActiveWorkoutUseCase(fixture.VerticalSlice, fixture.ActiveWorkout, new TaskSchedulerOffUiExecutor());
        var rehydrated = await useCase.ExecuteAsync();

        Assert.NotNull(rehydrated); Assert.Equal(started.Workout.SessionId, rehydrated.SessionId);
        Assert.Equal("persisted-before-process-exit", rehydrated.WorkoutName); Assert.Equal("safe draft", rehydrated.WorkoutNote);
        Assert.Equal(ActiveWorkoutLifecycle.ActiveDraft, fixture.ActiveWorkout.Lifecycle);
        Assert.Equal(WorkoutSaveState.Saved, fixture.ActiveWorkout.SaveState);
        Assert.Equal(0, fixture.ActiveWorkout.EditRevision); Assert.Equal(0, fixture.ActiveWorkout.LastSavedRevision);
        Assert.False(fixture.ActiveWorkout.HasUnsavedChanges);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(WorkoutStatus.Draft, (await db.WorkoutSessions.SingleAsync()).Status);
    }
}
