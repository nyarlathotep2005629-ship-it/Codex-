using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Views;
using WorkoutTracker.Infrastructure;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.App.Tests;

public sealed class MenuSaveUiRegressionTests
{
    [Fact(DisplayName = "REG-MENU-001 loaded Master Data view saves and reselects a multi-exercise Menu without a Dispatcher exception")]
    public async Task LoadedViewSavesAndReselectsMultiExerciseMenu()
    {
        await WpfTestApplication.RunAsync(async _ =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "WorkoutTracker.MenuSaveUiRegression", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            ServiceProvider? provider = null;
            Window? window = null;
            var dispatcherExceptions = new List<Exception>();
            DispatcherUnhandledExceptionEventHandler handler = (_, e) =>
            {
                dispatcherExceptions.Add(e.Exception);
                e.Handled = true;
            };

            System.Windows.Application.Current.DispatcherUnhandledException += handler;
            try
            {
                var services = new ServiceCollection();
                services.AddApplication();
                services.AddInfrastructure(Path.Combine(directory, "test.db"));
                services.AddSingleton<ActiveWorkoutViewModel>();
                services.AddSingleton<MasterDataViewModel>();
                provider = services.BuildServiceProvider();

                await provider.GetRequiredService<IDatabaseInitializer>().InitializeFreshDatabaseAsync();
                var viewModel = provider.GetRequiredService<MasterDataViewModel>();
                await viewModel.InitializeAsync();

                window = new Window
                {
                    Content = new MasterDataView { DataContext = viewModel },
                    ShowInTaskbar = false,
                    Opacity = 0,
                    Width = 1200,
                    Height = 800
                };
                window.Show();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                viewModel.MuscleName = "胸";
                await viewModel.SaveMuscleGroupAsync();
                foreach (var exerciseName in new[] { "ベンチプレス", "ミリタリープレス", "足上げベンチ", "懸垂", "ディップス" })
                {
                    viewModel.NewExerciseCommand.Execute(null);
                    viewModel.ExerciseName = exerciseName;
                    viewModel.ExercisePrimaryMuscle = viewModel.MuscleGroups.Single();
                    await viewModel.SaveExerciseAsync();
                }

                viewModel.NewMenuCommand.Execute(null);
                viewModel.MenuName = "土曜日の胸の日";
                foreach (var exercise in viewModel.Exercises)
                {
                    viewModel.ExerciseToAdd = exercise;
                    viewModel.AddMenuExerciseCommand.Execute(null);
                }

                await viewModel.SaveMenuAsync();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.Empty(dispatcherExceptions);
                Assert.False(viewModel.HasError);
                Assert.Single(viewModel.Menus);
                Assert.Equal(viewModel.Menus.Single(), viewModel.SelectedMenu);
                Assert.Equal("土曜日の胸の日", viewModel.MenuName);
                Assert.Equal(5, viewModel.MenuExercises.Count);
            }
            finally
            {
                System.Windows.Application.Current.DispatcherUnhandledException -= handler;
                window?.Close();
                if (provider is not null) await provider.DisposeAsync();
                SqliteConnection.ClearAllPools();
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
        });
    }

    [Fact(DisplayName = "REG-MENU-002 fatal error presentation is idempotent for repeated Dispatcher failures")]
    public void FatalErrorPresentationIsIdempotent()
    {
        var gate = new FatalErrorGate();

        Assert.True(gate.TryEnter());
        Assert.False(gate.TryEnter());
        Assert.False(gate.TryEnter());
    }

    [Fact(DisplayName = "REG-MENU-003 fatal UI errors are persisted with their inquiry ID")]
    public void FatalUiErrorLogContainsInquiryIdAndException()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkoutTracker.UiErrorLog", Guid.NewGuid().ToString("N"));
        var correlationId = Guid.NewGuid();
        try
        {
            IUiExceptionReporter reporter = new LocalFileUiExceptionReporter(directory);
            reporter.Report(new InvalidOperationException("test failure"), "DispatcherUnhandledException", correlationId);

            var log = File.ReadAllText(Path.Combine(directory, "ui-errors.log"));
            Assert.Contains(correlationId.ToString(), log, StringComparison.Ordinal);
            Assert.Contains("InvalidOperationException", log, StringComparison.Ordinal);
            Assert.Contains("test failure", log, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
