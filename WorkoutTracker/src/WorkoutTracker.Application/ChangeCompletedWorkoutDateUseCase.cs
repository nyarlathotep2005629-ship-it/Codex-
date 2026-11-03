namespace WorkoutTracker.Application;

public sealed class ChangeCompletedWorkoutDateUseCase(
    ICompletedWorkoutDatePersistence persistence,
    IClock clock,
    IOffUiExecutor executor,
    IWriteAdmissionGate? writeGate = null)
{
    public Task<bool> ExecuteAsync(Guid sessionId, DateOnly workoutDate, CancellationToken cancellationToken = default)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("Session ID must not be empty.", nameof(sessionId));
        var today = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime().DateTime);
        if (workoutDate < new DateOnly(1900, 1, 1) || workoutDate > today)
            throw new ArgumentOutOfRangeException(nameof(workoutDate), "Workout date must be between 1900-01-01 and the local current date.");

        return executor.RunAsync(async token =>
        {
            using var lease = writeGate?.EnterPublicWrite();
            return await persistence.ChangeDateAsync(sessionId, workoutDate, clock.UtcNow, token);
        }, cancellationToken);
    }
}
