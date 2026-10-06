using WorkoutTracker.Application;
using WorkoutTracker.App.ViewModels;

namespace WorkoutTracker.App.Services;

public sealed class WpfApplicationStateReloader(
    ActiveWorkoutViewModel activeWorkout,
    MasterDataViewModel masterData,
    DashboardViewModel dashboard,
    StatisticsViewModel statistics,
    HistoryViewModel history,
    CalendarViewModel calendar,
    SettingsService settings,
    ThemeService theme) : IApplicationStateReloader
{
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        await activeWorkout.ReloadFromDatabaseAsync(cancellationToken);
        var restoredSettings = await settings.GetAsync(cancellationToken);
        theme.Apply(restoredSettings.ThemeMode);
        await masterData.RefreshAsync();
        await dashboard.ReloadAsync();
        await statistics.ReloadAsync();
        await history.ReloadAsync();
        await calendar.ReloadAsync();
    }
}
