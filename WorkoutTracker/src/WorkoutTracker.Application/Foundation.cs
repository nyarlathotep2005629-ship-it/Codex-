namespace WorkoutTracker.Application;

public interface IClock { DateTimeOffset UtcNow { get; } }

public readonly record struct Result(bool IsSuccess, string? ErrorCode = null, string? ErrorMessage = null)
{
    public static Result Success() => new(true);
    public static Result Failure(string code, string message) => new(false, code, message);
}

public interface IApplicationTransactionCoordinator
{
    Task ExecuteAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}

public interface IOffUiExecutor
{
    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default);
    Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default);
}

public sealed class TaskSchedulerOffUiExecutor : IOffUiExecutor
{
    public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => Task.Run(() => operation(cancellationToken), cancellationToken);
    public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => Task.Run(() => operation(cancellationToken), cancellationToken);
}
