using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class StandardCatalogTests
{
    [Fact]
    public async Task FreshDatabaseHasApprovedCatalogAndSecondSeedIsIdempotent()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        Assert.Equal(12, StandardExerciseCatalog.Groups.Count);
        Assert.Equal(100, StandardExerciseCatalog.Groups.Sum(x => x.Exercises.Length));
        Assert.Equal(100, StandardExerciseCatalog.Groups.SelectMany(x => x.Exercises).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var first = await fixture.CatalogSeeder.SeedAsync();
        var second = await fixture.CatalogSeeder.SeedAsync();
        Assert.Equal(12, first.AddedMuscleGroups);
        Assert.Equal(100, first.AddedExercises);
        Assert.Empty(first.Conflicts);
        Assert.Equal(0, second.AddedMuscleGroups);
        Assert.Equal(0, second.AddedExercises);
        await using var db = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(12, await db.MuscleGroups.CountAsync());
        Assert.Equal(100, await db.Exercises.CountAsync());
    }

    [Fact]
    public async Task ExistingUserExerciseAndEnglishAliasKeepTheirIdsAndReferences()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var muscleId = Guid.NewGuid();
        var exerciseId = Guid.NewGuid();
        var menuId = Guid.NewGuid();
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.AddRange(new MuscleGroup(muscleId, "胸", 0, now), new Exercise(exerciseId, "Bench Press", muscleId, "個人メモ", now),
                new WorkoutMenu(menuId, "胸の日", null, now), new MenuExercise(Guid.NewGuid(), menuId, exerciseId, 0, 3, null, now));
            await db.SaveChangesAsync();
        }

        var result = await fixture.CatalogSeeder.SeedAsync();
        Assert.Equal(11, result.AddedMuscleGroups);
        Assert.Equal(99, result.AddedExercises);
        Assert.Equal(1, result.ReusedExercises);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        var original = await verify.Exercises.SingleAsync(x => x.Id == exerciseId);
        Assert.Equal("Bench Press", original.Name);
        Assert.Equal("個人メモ", original.Note);
        Assert.Equal(exerciseId, (await verify.MenuExercises.SingleAsync()).ExerciseId);
        Assert.Equal(100, await verify.Exercises.CountAsync());
    }

    [Fact]
    public async Task StableIdCollisionDoesNotOverwriteUserExercise()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var id = StandardExerciseCatalog.StableId("exercise", "ベンチプレス");
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            var muscle = new MuscleGroup(Guid.NewGuid(), "胸", 0, now);
            db.AddRange(muscle, new Exercise(id, "個人用の別種目", muscle.Id, null, now));
            await db.SaveChangesAsync();
        }

        var result = await fixture.CatalogSeeder.SeedAsync();
        Assert.Single(result.Conflicts);
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal("個人用の別種目", (await verify.Exercises.SingleAsync(x => x.Id == id)).Name);
        Assert.Equal(100, await verify.Exercises.CountAsync());
    }

    [Fact]
    public async Task AmbiguousJapaneseAndEnglishExistingIdsRemainUntouched()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.Initializer.InitializeFreshDatabaseAsync();
        var now = DateTimeOffset.UtcNow;
        var muscle = new MuscleGroup(Guid.NewGuid(), "胸", 0, now);
        var english = new Exercise(Guid.NewGuid(), "Bench Press", muscle.Id, null, now);
        var japanese = new Exercise(Guid.NewGuid(), "ベンチプレス", muscle.Id, null, now);
        await using (var db = await fixture.Factory.CreateDbContextAsync())
        {
            db.AddRange(muscle, english, japanese);
            await db.SaveChangesAsync();
        }

        var result = await fixture.CatalogSeeder.SeedAsync();
        Assert.Contains(result.Conflicts, x => x.Contains("ベンチプレス"));
        await using var verify = await fixture.Factory.CreateDbContextAsync();
        Assert.Equal(101, await verify.Exercises.CountAsync());
        Assert.Equal("Bench Press", (await verify.Exercises.SingleAsync(x => x.Id == english.Id)).Name);
        Assert.Equal("ベンチプレス", (await verify.Exercises.SingleAsync(x => x.Id == japanese.Id)).Name);
    }
}
