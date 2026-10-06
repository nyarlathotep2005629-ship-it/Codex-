using System.IO;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.Application;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Views;
using WorkoutTracker.Infrastructure;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.App.Tests;

public sealed class MasterDataViewModelTests
{
    [Fact(DisplayName = "MST-001 MST-006 and VAL-009 Master Data ViewModel creates Exercise and Menu with planned Sets")]
    public async Task CreatesExerciseAndMenu()
    {
        await using var h = await MasterHarness.CreateAsync(); var vm = h.ViewModel;
        vm.MuscleName = "Chest"; await vm.SaveMuscleGroupAsync();
        vm.NewExerciseCommand.Execute(null); vm.ExerciseName = "Bench"; vm.ExercisePrimaryMuscle = vm.MuscleGroups.Single(); await vm.SaveExerciseAsync();
        vm.NewMenuCommand.Execute(null); vm.MenuName = "Push"; vm.ExerciseToAdd = vm.Exercises.Single(); vm.AddMenuExerciseCommand.Execute(null); vm.MenuExercises.Single().DefaultSetCount = 5; await vm.SaveMenuAsync();
        Assert.False(vm.HasError); Assert.Single(vm.Menus); Assert.Equal(5, vm.Menus.Single().PlannedSetCount); Assert.Equal("Bench", vm.Menus.Single().ExerciseSummary);
    }

    [Fact(DisplayName = "UI-002 input validation is shown inline and does not offer Retry")]
    public async Task InputValidationIsInlineWithoutRetry()
    {
        await using var h = await MasterHarness.CreateAsync(); var vm = h.ViewModel;
        vm.MuscleName = "";

        await vm.SaveMuscleGroupAsync();

        Assert.Equal("部位名を入力してください。", vm.MuscleNameError);
        Assert.Null(vm.OperationError);
        Assert.False(vm.OperationCanRetry);
    }

    [Fact(DisplayName = "MST-003 Master Data ViewModel preserves hidden Exercise in editor list and excludes it from active picker")]
    public async Task HidesExerciseFromPicker()
    {
        await using var h = await MasterHarness.CreateAsync(); var vm = h.ViewModel;
        vm.MuscleName = "Back"; await vm.SaveMuscleGroupAsync(); vm.NewExerciseCommand.Execute(null); vm.ExerciseName = "Row"; vm.ExercisePrimaryMuscle = vm.MuscleGroups.Single(); await vm.SaveExerciseAsync();
        await vm.SetSelectedExerciseActiveAsync(false);
        Assert.Single(vm.Exercises); Assert.False(vm.Exercises.Single().IsActive); Assert.Empty(vm.ActiveExercises);
    }

    [Fact(DisplayName = "MST-001 Exercise search matches name primary and secondary muscle without showing inactive choices")]
    public async Task ExerciseSearch()
    {
        await using var h = await MasterHarness.CreateAsync(); var vm = h.ViewModel;
        vm.MuscleName = "Chest"; await vm.SaveMuscleGroupAsync(); vm.NewMuscleGroupCommand.Execute(null); vm.MuscleName = "Triceps"; await vm.SaveMuscleGroupAsync();
        vm.NewExerciseCommand.Execute(null); vm.ExerciseName = "Bench Press"; vm.ExercisePrimaryMuscle = vm.MuscleGroups.Single(x => x.Name == "Chest"); vm.SecondaryMuscles.Single(x => x.Name == "Triceps").IsSelected = true; await vm.SaveExerciseAsync();
        vm.ExerciseSearchText = "triceps"; Assert.Equal("Bench Press", vm.FilteredExercises.Single().Name); Assert.Equal("Bench Press", vm.ActiveExercises.Single().Name);
        await vm.SetSelectedExerciseActiveAsync(false); Assert.Single(vm.FilteredExercises); Assert.Empty(vm.ActiveExercises);
    }

    [Fact(DisplayName = "MST-009 Menu editor Up Down ordering is keyboard-operable before transactional save")]
    public async Task MenuOrderCommands()
    {
        await using var h = await MasterHarness.CreateAsync(); var vm = h.ViewModel;
        vm.MuscleName = "Legs"; await vm.SaveMuscleGroupAsync();
        foreach (var name in new[] { "A", "B", "C" }) { vm.NewExerciseCommand.Execute(null); vm.ExerciseName = name; vm.ExercisePrimaryMuscle = vm.MuscleGroups.Single(); await vm.SaveExerciseAsync(); }
        vm.NewMenuCommand.Execute(null); foreach (var exercise in vm.Exercises.OrderBy(x => x.Name)) { vm.ExerciseToAdd = exercise; vm.AddMenuExerciseCommand.Execute(null); }
        vm.MoveMenuExercise(vm.MenuExercises[2], -1); vm.MoveMenuExercise(vm.MenuExercises[1], -1);
        Assert.Equal(["C", "A", "B"], vm.MenuExercises.Select(x => x.ExerciseName));
    }

    [Fact(DisplayName = "APP-013 Menu delete confirmation contains the required active Workout non-impact explanation")]
    public void MenuDeleteExplanation() => Assert.Equal("進行中のトレーニング記録には影響しません。", MasterDataView.MenuDeleteExplanation);

    [Fact(DisplayName = "ARC-001 Master Data ViewModel composition validates singleton dependencies")]
    public void Composition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkoutTracker.App.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection(); services.AddApplication(); services.AddInfrastructure(Path.Combine(directory, "test.db")); services.AddSingleton<ActiveWorkoutViewModel>(); services.AddSingleton<MasterDataViewModel>(); services.AddSingleton<ShellViewModel>();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }); Assert.NotNull(provider.GetRequiredService<MasterDataViewModel>()); Assert.NotNull(provider.GetRequiredService<ShellViewModel>());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private sealed class MasterHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        private readonly string _directory;
        private MasterHarness(ServiceProvider provider, string directory, MasterDataViewModel viewModel) { _provider = provider; _directory = directory; ViewModel = viewModel; }
        public MasterDataViewModel ViewModel { get; }
        public static async Task<MasterHarness> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "WorkoutTracker.App.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            var services = new ServiceCollection(); services.AddApplication(); services.AddInfrastructure(Path.Combine(directory, "test.db")); services.AddSingleton<ActiveWorkoutViewModel>(); services.AddSingleton<MasterDataViewModel>();
            var provider = services.BuildServiceProvider(); await provider.GetRequiredService<IDatabaseInitializer>().InitializeFreshDatabaseAsync(); var vm = provider.GetRequiredService<MasterDataViewModel>(); await vm.InitializeAsync(); return new(provider, directory, vm);
        }
        public async ValueTask DisposeAsync() { await _provider.DisposeAsync(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
