using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

internal sealed class CompletedWorkoutDatePersistence(IDbContextFactory<WorkoutTrackerDbContext> factory) : ICompletedWorkoutDatePersistence
{
    public async Task<bool> ChangeDateAsync(Guid sessionId, DateOnly workoutDate, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var session = await db.WorkoutSessions.SingleOrDefaultAsync(
                x => x.Id == sessionId && x.Status == WorkoutStatus.Completed, cancellationToken);
            if (session is null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
                return false;
            }

            session.ChangeCompletedWorkoutDate(workoutDate, now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
