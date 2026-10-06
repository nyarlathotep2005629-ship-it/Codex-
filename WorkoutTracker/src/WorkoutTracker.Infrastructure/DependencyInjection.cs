using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.Application;
using WorkoutTracker.Infrastructure.Persistence;
using WorkoutTracker.Infrastructure.Repositories;

namespace WorkoutTracker.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, string databasePath)
    {
        databasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(databasePath);
        if (directory is not null) Directory.CreateDirectory(directory);
        var connectionString = SqliteConnectionStringFactory.Create(databasePath);
        services.AddSingleton<ForeignKeyVerificationInterceptor>();
        services.AddSingleton(new DatabaseLocation(databasePath));
        services.AddDbContextFactory<WorkoutTrackerDbContext>((provider, options) =>
            options.UseSqlite(connectionString).AddInterceptors(provider.GetRequiredService<ForeignKeyVerificationInterceptor>()));
        services.AddScoped<IWriteContextAccessor, WriteContextAccessor>();
        services.AddScoped<IApplicationTransactionCoordinator, ApplicationTransactionCoordinator>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IWorkoutSessionRepository, WorkoutSessionRepository>();
        services.AddSingleton<IExerciseRepository, ExerciseRepository>();
        services.AddSingleton<IWorkoutMenuRepository, WorkoutMenuRepository>();
        services.AddSingleton<IWorkoutVerticalSlicePersistence, WorkoutVerticalSlicePersistence>();
        services.AddSingleton<IMasterDataPersistence, MasterDataPersistence>();
        services.AddSingleton<IHistoryCalendarQuery, HistoryCalendarQuery>();
        services.AddSingleton<IAnalyticsQuery, AnalyticsQuery>();
        services.AddSingleton<ITransferService, TransferService>();
        services.AddSingleton<IAppSettingsStore, AppSettingsStore>();
        services.AddSingleton<IBackupInfrastructure, BackupInfrastructure>();
        services.AddSingleton<IRestoreFailureInjector, NoOpRestoreFailureInjector>();
        services.AddSingleton<IRestoreInfrastructure, RestoreInfrastructure>();
        services.AddSingleton<IMigrationFailureInjector, NoOpMigrationFailureInjector>();
        services.AddSingleton<IMigrationProcessLockProvider, NamedMigrationProcessLockProvider>();
        services.AddSingleton<SqliteTableRebuilder>();
        services.AddSingleton<IStartupMigrationCoordinator, StartupMigrationCoordinator>();
        services.AddSingleton<IDatabaseInitializer, DatabaseInitializer>();
        return services;
    }
}
