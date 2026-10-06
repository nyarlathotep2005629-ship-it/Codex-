using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.Application;
using WorkoutTracker.Infrastructure;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure.Tests;

internal sealed class DatabaseTestFixture : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    public DatabaseTestFixture(Action<IServiceCollection>? configure = null)
    {
        Directory = Path.Combine(Path.GetTempPath(), "WorkoutTracker.Tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(Directory);
        DatabasePath = Path.Combine(Directory, "test.db");
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(DatabasePath);
        configure?.Invoke(services);
        _provider = services.BuildServiceProvider();
        Factory = _provider.GetRequiredService<IDbContextFactory<WorkoutTrackerDbContext>>();
        Sessions = _provider.GetRequiredService<IWorkoutSessionRepository>();
        Transactions = _provider.GetRequiredService<IApplicationTransactionCoordinator>();
        VerticalSlice = _provider.GetRequiredService<IWorkoutVerticalSlicePersistence>();
        MasterData = _provider.GetRequiredService<IMasterDataPersistence>();
        HistoryCalendar = _provider.GetRequiredService<IHistoryCalendarQuery>();
        Analytics = _provider.GetRequiredService<IAnalyticsQuery>();
        Settings = _provider.GetRequiredService<SettingsService>();
        Backup = _provider.GetRequiredService<BackupService>();
        StartWorkout = _provider.GetRequiredService<StartWorkoutUseCase>();
        AutoSave = _provider.GetRequiredService<AutoSaveCoordinator>();
        ActiveWorkout = _provider.GetRequiredService<ActiveWorkoutState>();
        Restore = _provider.GetRequiredService<RestoreService>();
        WriteGate = _provider.GetRequiredService<IWriteAdmissionGate>();
        Initializer = _provider.GetRequiredService<IDatabaseInitializer>();
        TableRebuilder = _provider.GetRequiredService<SqliteTableRebuilder>();
        Transfer = _provider.GetRequiredService<ITransferService>();
    }
    public string Directory { get; }
    public string DatabasePath { get; }
    public IDbContextFactory<WorkoutTrackerDbContext> Factory { get; }
    public IWorkoutSessionRepository Sessions { get; }
    public IApplicationTransactionCoordinator Transactions { get; }
    public IWorkoutVerticalSlicePersistence VerticalSlice { get; }
    public IMasterDataPersistence MasterData { get; }
    public IHistoryCalendarQuery HistoryCalendar { get; }
    public IAnalyticsQuery Analytics { get; }
    public SettingsService Settings { get; }
    public BackupService Backup { get; }
    public StartWorkoutUseCase StartWorkout { get; }
    public AutoSaveCoordinator AutoSave { get; }
    public ActiveWorkoutState ActiveWorkout { get; }
    public RestoreService Restore { get; }
    public IWriteAdmissionGate WriteGate { get; }
    public IDatabaseInitializer Initializer { get; }
    public SqliteTableRebuilder TableRebuilder { get; }
    public ITransferService Transfer { get; }
    public async Task MigrateAsync() { await using var db = await Factory.CreateDbContextAsync(); await db.Database.MigrateAsync(); }
    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true);
    }
}
