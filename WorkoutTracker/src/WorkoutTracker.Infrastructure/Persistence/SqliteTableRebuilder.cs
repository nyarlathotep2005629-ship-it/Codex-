using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace WorkoutTracker.Infrastructure.Persistence;

public sealed record SqliteTableRebuildPlan(
    string TableName,
    string TemporaryTableName,
    string CreateTemporaryTableSql,
    string CopyDataSql,
    IReadOnlyList<string> RecreateIndexAndTriggerSql);

public sealed class SqliteTableRebuilder(DatabaseLocation location)
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant);

    public Task ExecuteAsync(SqliteTableRebuildPlan plan, CancellationToken cancellationToken = default) =>
        Task.Run(() => ExecuteOffUiAsync(plan, cancellationToken), cancellationToken);

    private async Task ExecuteOffUiAsync(SqliteTableRebuildPlan plan, CancellationToken cancellationToken)
    {
        ValidateIdentifier(plan.TableName);
        ValidateIdentifier(plan.TemporaryTableName);
        var archivedTableName = $"{plan.TableName}__rebuild_old";
        ValidateIdentifier(archivedTableName);

        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = location.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = 30
        }.ToString());
        await connection.OpenAsync(cancellationToken);
        await ExecuteAsync(connection, null, "PRAGMA foreign_keys=OFF;", cancellationToken);

        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await ExecuteAsync(connection, transaction, plan.CreateTemporaryTableSql, cancellationToken);
            await ExecuteAsync(connection, transaction, plan.CopyDataSql, cancellationToken);

            var oldCount = await ScalarInt64Async(connection, transaction, $"SELECT COUNT(*) FROM {Quote(plan.TableName)};", cancellationToken);
            var newCount = await ScalarInt64Async(connection, transaction, $"SELECT COUNT(*) FROM {Quote(plan.TemporaryTableName)};", cancellationToken);
            if (oldCount != newCount) throw new InvalidOperationException($"Table rebuild row count mismatch: {oldCount} != {newCount}.");

            await EnsureForeignKeysAsync(connection, transaction, plan.TemporaryTableName, cancellationToken);
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {Quote(plan.TableName)} RENAME TO {Quote(archivedTableName)};", cancellationToken);
            await ExecuteAsync(connection, transaction, $"ALTER TABLE {Quote(plan.TemporaryTableName)} RENAME TO {Quote(plan.TableName)};", cancellationToken);
            // The old table still owns its original index/trigger names after rename.
            // Dropping it inside the same transaction releases those names; rollback still restores everything atomically.
            await ExecuteAsync(connection, transaction, $"DROP TABLE {Quote(archivedTableName)};", cancellationToken);
            foreach (var sql in plan.RecreateIndexAndTriggerSql)
                await ExecuteAsync(connection, transaction, sql, cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON;", cancellationToken);
            await EnsureForeignKeysAsync(connection, null, null, cancellationToken);
            var quickCheck = await ScalarStringAsync(connection, null, "PRAGMA quick_check;", cancellationToken);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"quick_check failed after table rebuild: {quickCheck}.");
        }
        finally
        {
            if (connection.State == System.Data.ConnectionState.Open)
                await ExecuteAsync(connection, null, "PRAGMA foreign_keys=ON;", CancellationToken.None);
        }
    }

    private static async Task EnsureForeignKeysAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string? tableName, CancellationToken cancellationToken)
    {
        var sql = tableName is null ? "PRAGMA foreign_key_check;" : $"PRAGMA foreign_key_check({Quote(tableName)});";
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("foreign_key_check failed during table rebuild.");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> ScalarInt64Async(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async Task<string?> ScalarStringAsync(SqliteConnection connection, System.Data.Common.DbTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction?)transaction;
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void ValidateIdentifier(string identifier)
    {
        if (!IdentifierPattern.IsMatch(identifier)) throw new ArgumentException("SQLite identifier is invalid.", nameof(identifier));
    }

    private static string Quote(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";
}
