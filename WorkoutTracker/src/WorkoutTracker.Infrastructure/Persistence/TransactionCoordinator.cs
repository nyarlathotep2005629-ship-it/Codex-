using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;

namespace WorkoutTracker.Infrastructure.Persistence;

internal interface IWriteContextAccessor
{
    WorkoutTrackerDbContext Current { get; }
    void Set(WorkoutTrackerDbContext context);
    void Clear();
}

internal sealed class WriteContextAccessor : IWriteContextAccessor
{
    private WorkoutTrackerDbContext? _current;
    public WorkoutTrackerDbContext Current => _current ?? throw new InvalidOperationException("A write repository must run inside IApplicationTransactionCoordinator.");
    public void Set(WorkoutTrackerDbContext context) => _current = context;
    public void Clear() => _current = null;
}

internal sealed class ApplicationTransactionCoordinator(IDbContextFactory<WorkoutTrackerDbContext> factory, IWriteContextAccessor accessor) : IApplicationTransactionCoordinator
{
    public async Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        accessor.Set(db);
        try
        {
            await operation(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            accessor.Clear();
        }
    }
}

internal sealed class SystemClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
