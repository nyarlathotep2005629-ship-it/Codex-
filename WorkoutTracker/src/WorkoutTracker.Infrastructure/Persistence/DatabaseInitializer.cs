namespace WorkoutTracker.Infrastructure.Persistence;

public interface IDatabaseInitializer
{
    Task InitializeFreshDatabaseAsync(CancellationToken cancellationToken = default);
}

internal sealed class DatabaseInitializer(IStartupMigrationCoordinator migration) : IDatabaseInitializer
{
    public Task InitializeFreshDatabaseAsync(CancellationToken cancellationToken = default) => migration.InitializeAsync(cancellationToken);
}
