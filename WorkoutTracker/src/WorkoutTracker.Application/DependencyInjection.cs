using Microsoft.Extensions.DependencyInjection;

namespace WorkoutTracker.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddSingleton<ActiveWorkoutState>();
        services.AddSingleton<IWriteAdmissionGate, WriteAdmissionGate>();
        services.AddSingleton<IOffUiExecutor, TaskSchedulerOffUiExecutor>();
        services.AddSingleton<IAutoSaveDelay, SystemAutoSaveDelay>();
        services.AddSingleton<AutoSaveCoordinator>();
        services.AddSingleton<RestTimerState>();
        services.AddSingleton<ShutdownCoordinator>();
        services.AddSingleton<IApplicationStateReloader, ApplicationStateReloader>();
        services.AddTransient<StartWorkoutUseCase>();
        services.AddTransient<LoadActiveWorkoutUseCase>();
        services.AddTransient<GetPreviousPerformanceUseCase>();
        services.AddTransient<FinishWorkoutUseCase>();
        services.AddTransient<DiscardWorkoutUseCase>();
        services.AddSingleton<MasterDataService>();
        services.AddSingleton<HistoryCalendarService>();
        services.AddSingleton<AnalyticsService>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<RestoreService>();
        return services;
    }
}
