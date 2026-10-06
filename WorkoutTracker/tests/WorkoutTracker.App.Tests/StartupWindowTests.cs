using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using WorkoutTracker.Application;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Views;
using WorkoutTracker.Infrastructure;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.App.Tests;

public sealed class StartupWindowTests
{
    [Fact(DisplayName = "Human Acceptance startup and Settings maintenance progress bindings support read-only ViewModel properties")]
    public async Task MaintenanceProgressBindingsAreOneWayAndMainWindowCanBeShown()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"WorkoutTracker-StartupWindow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            await WpfTestApplication.RunAsync(async application =>
            {
                Assert.NotNull(application.TryFindResource("PageTitleStyle"));
                using var provider = CreateProvider(directory);
                await provider.GetRequiredService<IDatabaseInitializer>().InitializeFreshDatabaseAsync();
                await provider.GetRequiredService<ShellViewModel>().InitializeAsync();
                var window = provider.GetRequiredService<MainWindow>();
                try
                {
                    var shellProgress = Assert.IsType<ProgressBar>(window.FindName("MaintenanceProgressBar"));
                    Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(shellProgress, ProgressBar.ValueProperty)!.Mode);

                    var settings = new SettingsView();
                    var settingsProgress = Assert.IsType<ProgressBar>(settings.FindName("MaintenanceProgressBar"));
                    Assert.Equal(BindingMode.OneWay, BindingOperations.GetBinding(settingsProgress, ProgressBar.ValueProperty)!.Mode);

                    window.ShowInTaskbar = false;
                    window.Opacity = 0;
                    window.Show();
                    await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                }
                finally
                {
                    window.Close();
                    SqliteConnection.ClearAllPools();
                }
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static ServiceProvider CreateProvider(string directory)
    {
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(Path.Combine(directory, "test.db"));
        services.AddSingleton<ActiveWorkoutViewModel>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider();
    }
}
