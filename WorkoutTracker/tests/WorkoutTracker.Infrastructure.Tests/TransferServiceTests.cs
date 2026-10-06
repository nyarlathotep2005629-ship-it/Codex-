using System.IO.Compression;
using System.Text;
using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class TransferServiceTests
{
    [Fact(DisplayName = "M2-TRANSFER-001 export creates a validated wtb package and completed-only payload")]
    public async Task ExportCreatesValidatedPackage()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        await SeedCompletedAsync(fixture);
        var path = Path.Combine(fixture.Directory, "WorkoutTracker_test.wtb");

        var exported = await fixture.Transfer.ExportAsync(path, "Windows", "test");
        Assert.True(File.Exists(path));
        Assert.Equal(1, exported.Manifest.Counts.WorkoutSessions);
        Assert.Equal(1, exported.Manifest.Counts.WorkoutSets);
        var plan = await fixture.Transfer.CreateImportPlanAsync(path);
        Assert.Empty(plan.Conflicts);
        Assert.Equal(plan.Package.Manifest.PackageId, exported.Manifest.PackageId);
    }

    [Fact(DisplayName = "M2-TRANSFER-002 import is bidirectional-core compatible and idempotent")]
    public async Task ImportAndReimportAreIdempotent()
    {
        await using var source = new DatabaseTestFixture();
        await source.Initializer.InitializeFreshDatabaseAsync();
        await SeedCompletedAsync(source);
        var path = Path.Combine(source.Directory, "WorkoutTracker_test.wtb");
        await source.Transfer.ExportAsync(path, "Windows", "test");

        await using var target = new DatabaseTestFixture();
        await target.Initializer.InitializeFreshDatabaseAsync();
        var plan = await target.Transfer.CreateImportPlanAsync(path);
        Assert.True(plan.CanCommit);
        var first = await target.Transfer.ImportAsync(plan, []);
        Assert.Equal(4, first.Added);

        var secondPlan = await target.Transfer.CreateImportPlanAsync(path);
        Assert.Empty(secondPlan.NewItems);
        Assert.Empty(secondPlan.Conflicts);
        Assert.NotEmpty(secondPlan.UnchangedItems);
        var second = await target.Transfer.ImportAsync(secondPlan, []);
        Assert.Equal(0, second.Added);
        await using var db = await target.Factory.CreateDbContextAsync();
        Assert.Equal(1, await db.WorkoutSessions.CountAsync());
        Assert.Equal(1, await db.WorkoutSets.CountAsync());
    }

    [Fact(DisplayName = "M2-TRANSFER-003 differing same Guid becomes a conflict before write")]
    public async Task SameGuidDifferenceBecomesConflict()
    {
        await using var source = new DatabaseTestFixture();
        await source.Initializer.InitializeFreshDatabaseAsync();
        var muscleId = Guid.NewGuid();
        await using (var db = await source.Factory.CreateDbContextAsync())
        {
            db.MuscleGroups.Add(new MuscleGroup(muscleId, "胸", 0, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var path = Path.Combine(source.Directory, "WorkoutTracker_test.wtb");
        await source.Transfer.ExportAsync(path, "Windows", "test");

        await using var target = new DatabaseTestFixture();
        await target.Initializer.InitializeFreshDatabaseAsync();
        await using (var db = await target.Factory.CreateDbContextAsync())
        {
            db.MuscleGroups.Add(new MuscleGroup(muscleId, "背中", 0, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        }
        var plan = await target.Transfer.CreateImportPlanAsync(path);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(TransferAggregateKind.MuscleGroup, conflict.Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(() => target.Transfer.ImportAsync(plan, []));
    }

    [Fact(DisplayName = "M2-TRANSFER-004 payload hash failure is rejected without database write")]
    public async Task InvalidPayloadHashIsRejectedWithoutDatabaseWrite()
    {
        await using var source = new DatabaseTestFixture();
        await source.Initializer.InitializeFreshDatabaseAsync();
        await SeedCompletedAsync(source);
        var path = Path.Combine(source.Directory, "WorkoutTracker_corrupt.wtb");
        await source.Transfer.ExportAsync(path, "Windows", "test");
        CorruptManifestHash(path);

        await using var target = new DatabaseTestFixture();
        await target.Initializer.InitializeFreshDatabaseAsync();
        await Assert.ThrowsAsync<TransferValidationException>(() => target.Transfer.CreateImportPlanAsync(path));
        await using var db = await target.Factory.CreateDbContextAsync();
        Assert.Equal(0, await db.MuscleGroups.CountAsync());
        Assert.Equal(0, await db.WorkoutSessions.CountAsync());
    }

    [Fact(DisplayName = "M2-TRANSFER-005 conflict import replaces the selected aggregate children")]
    public async Task ConflictImportReplacesSelectedMenuChildren()
    {
        var now = DateTimeOffset.UtcNow;
        var muscleId = Guid.NewGuid();
        var exerciseAId = Guid.NewGuid();
        var exerciseBId = Guid.NewGuid();
        var menuId = Guid.NewGuid();
        var sourceChildId = Guid.NewGuid();
        var targetOnlyChildId = Guid.NewGuid();

        await using var source = new DatabaseTestFixture();
        await source.Initializer.InitializeFreshDatabaseAsync();
        await using (var db = await source.Factory.CreateDbContextAsync())
        {
            var muscle = new MuscleGroup(muscleId, "胸", 0, now);
            var exerciseA = new Exercise(exerciseAId, "ベンチプレス", muscleId, null, now);
            var menu = new WorkoutMenu(menuId, "胸の日", null, now);
            var child = new MenuExercise(sourceChildId, menuId, exerciseAId, 0, 3, null, now);
            db.AddRange(muscle, exerciseA, menu, child);
            await db.SaveChangesAsync();
        }
        var path = Path.Combine(source.Directory, "WorkoutTracker_menu.wtb");
        await source.Transfer.ExportAsync(path, "Windows", "test");

        await using var target = new DatabaseTestFixture();
        await target.Initializer.InitializeFreshDatabaseAsync();
        await using (var db = await target.Factory.CreateDbContextAsync())
        {
            var muscle = new MuscleGroup(muscleId, "胸", 0, now);
            var exerciseA = new Exercise(exerciseAId, "ベンチプレス", muscleId, null, now);
            var exerciseB = new Exercise(exerciseBId, "スクワット", muscleId, null, now);
            var menu = new WorkoutMenu(menuId, "胸の日（ローカル変更）", null, now);
            var child = new MenuExercise(targetOnlyChildId, menuId, exerciseBId, 0, 1, null, now);
            db.AddRange(muscle, exerciseA, exerciseB, menu, child);
            await db.SaveChangesAsync();
        }

        var plan = await target.Transfer.CreateImportPlanAsync(path);
        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(TransferAggregateKind.WorkoutMenu, conflict.Kind);
        var result = await target.Transfer.ImportAsync(plan, [new ImportResolution(conflict.Kind, conflict.Id, true)]);
        Assert.Equal(1, result.Updated);

        await using var verify = await target.Factory.CreateDbContextAsync();
        var importedMenu = await verify.WorkoutMenus.Include(x => x.Exercises).SingleAsync(x => x.Id == menuId);
        var importedChild = Assert.Single(importedMenu.Exercises);
        Assert.Equal(exerciseAId, importedChild.ExerciseId);
        Assert.Equal(sourceChildId, importedChild.Id);
        Assert.Equal(3, importedChild.DefaultSetCount);
    }

    private static void CorruptManifestHash(string path)
    {
        using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
        var entry = archive.GetEntry("manifest.json")!;
        string json;
        using (var reader = new StreamReader(entry.Open(), Encoding.UTF8)) json = reader.ReadToEnd();
        var marker = "\"payloadSha256\":\"";
        var start = json.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        Assert.True(start >= marker.Length);
        var end = json.IndexOf('"', start);
        Assert.True(end > start);
        json = string.Concat(json.AsSpan(0, start), new string('0', end - start), json.AsSpan(end));
        entry.Delete();
        using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(json);
    }

    private static async Task SeedCompletedAsync(DatabaseTestFixture fixture)
    {
        var now = DateTimeOffset.UtcNow;
        await using var db = await fixture.Factory.CreateDbContextAsync();
        var muscle = new MuscleGroup(Guid.NewGuid(), "胸", 0, now);
        var exercise = new Exercise(Guid.NewGuid(), "ベンチプレス", muscle.Id, "", now);
        var menu = new WorkoutMenu(Guid.NewGuid(), "胸の日", null, now);
        var menuExercise = new MenuExercise(Guid.NewGuid(), menu.Id, exercise.Id, 0, 1, null, now);
        var session = new WorkoutSession(Guid.NewGuid(), new DateOnly(2026, 9, 22), menu.Id, "胸の日", null, now);
        session.Complete(now.AddMinutes(1));
        var workoutExercise = new WorkoutExercise(Guid.NewGuid(), session.Id, exercise.Id, exercise.Name, muscle.Id, muscle.Name, 0, null, now);
        var set = new WorkoutSet(Guid.NewGuid(), workoutExercise.Id, 1, 80m, 5, 8m, now);
        db.AddRange(muscle, exercise, menu, menuExercise, session, workoutExercise, set);
        await db.SaveChangesAsync();
    }
}
