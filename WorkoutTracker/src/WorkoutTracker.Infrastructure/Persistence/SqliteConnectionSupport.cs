using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace WorkoutTracker.Infrastructure.Persistence;

public static class SqliteConnectionStringFactory
{
    public static string Create(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        return new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 30
        }.ToString();
    }
}

internal sealed class ForeignKeyVerificationInterceptor : DbConnectionInterceptor
{
    private readonly object _sync = new();
    private readonly HashSet<DbConnection> _open = new(ReferenceEqualityComparer.Instance);
    private TaskCompletionSource _zero = CompletedSource();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) { TrackOpen(connection); Verify(connection); }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    { TrackOpen(connection); await VerifyAsync(connection, cancellationToken); }

    public override void ConnectionClosed(DbConnection connection, ConnectionEndEventData eventData) => TrackClosed(connection);
    public override Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData) { TrackClosed(connection); return Task.CompletedTask; }

    public async Task WaitForZeroAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            lock (_sync)
            {
                _open.RemoveWhere(connection => connection.State != System.Data.ConnectionState.Open);
                if (_open.Count == 0) return;
            }
            await Task.Delay(10, cancellationToken);
        }
    }

    private void TrackOpen(DbConnection connection)
    {
        lock (_sync) if (_open.Add(connection) && _open.Count == 1) _zero = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void TrackClosed(DbConnection connection)
    {
        lock (_sync) if (_open.Remove(connection) && _open.Count == 0) _zero.TrySetResult();
    }

    private static TaskCompletionSource CompletedSource()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); source.SetResult(); return source;
    }

    private static void Verify(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidOperationException("SQLite foreign key enforcement is disabled.");
    }

    private static async Task VerifyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys;";
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) != 1)
            throw new InvalidOperationException("SQLite foreign key enforcement is disabled.");
    }
}
