using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure.Tests;

public sealed class MigrationRecoveryTests
{
    [Fact(DisplayName = "MIG-001 compatible existing DB is backed up and migrated to the verified target")]
    public async Task ExistingDatabaseMigrationSucceeds()
    {
        await using var fixture = new DatabaseTestFixture();
        await CreateLegacyDatabaseAsync(fixture.DatabasePath);

        await fixture.Initializer.InitializeFreshDatabaseAsync();

        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM __EFMigrationsHistory;"));
        Assert.Equal("legacy-value", await ScalarStringAsync(fixture.DatabasePath, "SELECT Value FROM LegacyMarker WHERE Id=1;"));
        Assert.Equal("ok", await ScalarStringAsync(fixture.DatabasePath, "PRAGMA quick_check;"));
        var backup = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Directory, "Backups", "Migration"), "WorkoutTracker_PreMigration_*.db"));
        Assert.Equal(0, await ScalarIntAsync(backup, "PRAGMA user_version;"));
        Assert.Equal("legacy-value", await ScalarStringAsync(backup, "SELECT Value FROM LegacyMarker WHERE Id=1;"));
        var manifest = Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Directory, "Maintenance"), "MigrationOperation_*.json"));
        Assert.Equal("Completed", JsonDocument.Parse(await File.ReadAllTextAsync(manifest)).RootElement.GetProperty("State").GetString());
    }

    [Fact(DisplayName = "MIG-002 MIG-007 injected Migration failure preserves old DB and blocks normal startup with safe recovery details")]
    public async Task MigrationFailurePreservesOldDatabaseAndStopsStartup()
    {
        var injector = new MigrationInjector(MigrationFailurePoint.ExecuteMigration);
        await using var fixture = new DatabaseTestFixture(services => services.AddSingleton<IMigrationFailureInjector>(injector));
        await CreateLegacyDatabaseAsync(fixture.DatabasePath);

        var error = await Assert.ThrowsAsync<StartupRecoveryException>(() => fixture.Initializer.InitializeFreshDatabaseAsync());

        Assert.True(error.DatabaseRecovered); Assert.False(error.IsFatal);
        Assert.NotNull(error.EmergencyBackupPath); Assert.True(File.Exists(error.EmergencyBackupPath));
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal("legacy-value", await ScalarStringAsync(fixture.DatabasePath, "SELECT Value FROM LegacyMarker WHERE Id=1;"));
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='WorkoutSessions';"));
        var safe = error.ToSafeUserMessage();
        Assert.Contains(error.CorrelationId.ToString(), safe); Assert.Contains(error.EmergencyBackupPath, safe);
        Assert.DoesNotContain(nameof(IOException), safe); Assert.DoesNotContain("Injected", safe);
    }

    [Theory(DisplayName = "MIG-002 failure during Emergency backup or durable manifest preparation leaves Primary unchanged")]
    [InlineData(MigrationFailurePoint.CreateEmergencyBackup)]
    [InlineData(MigrationFailurePoint.WritePreparedManifest)]
    public async Task MigrationPreparationFailureIsSafe(MigrationFailurePoint point)
    {
        var injector = new MigrationInjector(point);
        await using var fixture = new DatabaseTestFixture(services => services.AddSingleton<IMigrationFailureInjector>(injector));
        await CreateLegacyDatabaseAsync(fixture.DatabasePath);

        var error = await Assert.ThrowsAsync<StartupRecoveryException>(() => fixture.Initializer.InitializeFreshDatabaseAsync());

        Assert.True(error.DatabaseRecovered); Assert.False(error.IsFatal);
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal("legacy-value", await ScalarStringAsync(fixture.DatabasePath, "SELECT Value FROM LegacyMarker WHERE Id=1;"));
        Assert.Equal("ok", await ScalarStringAsync(fixture.DatabasePath, "PRAGMA quick_check;"));
    }

    [Fact(DisplayName = "MIG-003 MIG-006 interrupted partial schema is restored from PreMigration backup")]
    public async Task InterruptedPartialSchemaRestoresEmergencyBackup()
    {
        await using var fixture = new DatabaseTestFixture();
        await CreateLegacyDatabaseAsync(fixture.DatabasePath);
        var operationId = Guid.NewGuid();
        var backup = await CreateMigrationBackupAsync(fixture, operationId);
        await ExecuteAsync(fixture.DatabasePath, "CREATE TABLE PartialMigration(Id INTEGER PRIMARY KEY); PRAGMA user_version=99;");
        var manifest = await WritePreparedManifestAsync(fixture, operationId, backup);

        var error = await Assert.ThrowsAsync<StartupRecoveryException>(() => fixture.Initializer.InitializeFreshDatabaseAsync());

        Assert.True(error.DatabaseRecovered); Assert.False(error.IsFatal);
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PartialMigration';"));
        Assert.Equal("legacy-value", await ScalarStringAsync(fixture.DatabasePath, "SELECT Value FROM LegacyMarker WHERE Id=1;"));
        Assert.Equal("ok", await ScalarStringAsync(fixture.DatabasePath, "PRAGMA quick_check;"));
        Assert.Equal("Recovered", JsonDocument.Parse(await File.ReadAllTextAsync(manifest)).RootElement.GetProperty("State").GetString());
    }

    [Fact(DisplayName = "MIG-003 complete target with unfinished manifest converges to target without rollback")]
    public async Task InterruptedCompleteTargetIsAcceptedOnlyAfterFullValidation()
    {
        await using var fixture = new DatabaseTestFixture();
        await fixture.MigrateAsync();
        var operationId = Guid.NewGuid();
        var backup = Path.Combine(fixture.Directory, "Backups", "Migration", $"old-{operationId}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        await CreateLegacyDatabaseAsync(backup);
        var manifest = await WritePreparedManifestAsync(fixture, operationId, backup);

        await fixture.Initializer.InitializeFreshDatabaseAsync();

        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "PRAGMA user_version;"));
        Assert.Equal("Completed", JsonDocument.Parse(await File.ReadAllTextAsync(manifest)).RootElement.GetProperty("State").GetString());
    }

    [Fact(DisplayName = "MIG-004 abandoned EF lock is removed only with a valid old DB and unfinished manifest")]
    public async Task AbandonedEfLockRequiresValidatedOldDatabase()
    {
        await using var fixture = new DatabaseTestFixture();
        await CreateLegacyDatabaseAsync(fixture.DatabasePath);
        await ExecuteAsync(fixture.DatabasePath, "CREATE TABLE __EFMigrationsLock(Id INTEGER PRIMARY KEY, Timestamp TEXT NOT NULL); INSERT INTO __EFMigrationsLock VALUES(1,'abandoned');");
        var operationId = Guid.NewGuid();
        var backup = await CreateMigrationBackupAsync(fixture, operationId);
        var manifest = await WritePreparedManifestAsync(fixture, operationId, backup);

        var error = await Assert.ThrowsAsync<StartupRecoveryException>(() => fixture.Initializer.InitializeFreshDatabaseAsync());

        Assert.True(error.DatabaseRecovered); Assert.False(error.IsFatal);
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM __EFMigrationsLock;"));
        Assert.Equal("Recovered", JsonDocument.Parse(await File.ReadAllTextAsync(manifest)).RootElement.GetProperty("State").GetString());
        Assert.Equal(TimeSpan.FromSeconds(30), MigrationSafetyLimits.LockTimeout);
        Assert.Equal(TimeSpan.FromSeconds(120), MigrationSafetyLimits.OverallTimeout);
    }

    [Fact(DisplayName = "MIG-004 process Migration lock uses exact 30 second timeout and stops before opening normal DB")]
    public async Task ProcessLockTimeoutStopsStartup()
    {
        var processLock = new RejectingProcessLock();
        await using var fixture = new DatabaseTestFixture(services => services.AddSingleton<IMigrationProcessLockProvider>(processLock));

        var error = await Assert.ThrowsAsync<StartupRecoveryException>(() => fixture.Initializer.InitializeFreshDatabaseAsync());

        Assert.Equal(TimeSpan.FromSeconds(30), processLock.ReceivedTimeout);
        Assert.False(error.IsFatal); Assert.False(error.DatabaseRecovered);
        Assert.False(File.Exists(fixture.DatabasePath));
    }

    [Fact(DisplayName = "MIG-005 table rebuild copies boundary data and recreates FK Index Trigger with no intermediate table")]
    public async Task TableRebuildIsAtomicAndComplete()
    {
        await using var fixture = new DatabaseTestFixture();
        await CreateRebuildFixtureAsync(fixture.DatabasePath);
        var plan = new SqliteTableRebuildPlan(
            "Child", "Child_rebuild_new",
            "CREATE TABLE Child_rebuild_new(Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL, Value INTEGER NOT NULL CHECK(Value BETWEEN -2147483648 AND 2147483647), Note TEXT NULL, FOREIGN KEY(ParentId) REFERENCES Parent(Id) ON DELETE CASCADE);",
            "INSERT INTO Child_rebuild_new(Id, ParentId, Value, Note) SELECT Id, ParentId, Value, Note FROM Child;",
            ["CREATE INDEX IX_Child_ParentId ON Child(ParentId);", "CREATE TRIGGER TR_Child_Audit AFTER INSERT ON Child BEGIN INSERT INTO Audit(ChildId) VALUES (NEW.Id); END;"]);

        await fixture.TableRebuilder.ExecuteAsync(plan);

        Assert.Equal(3, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM Child;"));
        Assert.Equal(-2147483648L, await ScalarLongAsync(fixture.DatabasePath, "SELECT MIN(Value) FROM Child;"));
        Assert.Equal(2147483647L, await ScalarLongAsync(fixture.DatabasePath, "SELECT MAX(Value) FROM Child;"));
        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Child_ParentId';"));
        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='TR_Child_Audit';"));
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Child_rebuild_new','Child__rebuild_old');"));
        Assert.Equal(0, await RowCountAsync(fixture.DatabasePath, "PRAGMA foreign_key_check;"));
        Assert.Equal("ok", await ScalarStringAsync(fixture.DatabasePath, "PRAGMA quick_check;"));
        await ExecuteAsync(fixture.DatabasePath, "INSERT INTO Child(Id,ParentId,Value,Note) VALUES(4,1,0,'trigger');");
        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM Audit WHERE ChildId=4;"));
    }

    [Fact(DisplayName = "MIG-002 MIG-005 table rebuild failure rolls back rename copy and schema as one transaction")]
    public async Task TableRebuildFailureRollsBackAllSteps()
    {
        await using var fixture = new DatabaseTestFixture();
        await CreateRebuildFixtureAsync(fixture.DatabasePath);
        var plan = new SqliteTableRebuildPlan(
            "Child", "Child_rebuild_new",
            "CREATE TABLE Child_rebuild_new(Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL, Value INTEGER NOT NULL, Note TEXT NULL, FOREIGN KEY(ParentId) REFERENCES Parent(Id));",
            "INSERT INTO Child_rebuild_new(Id, ParentId, Value, Note) SELECT Id, ParentId, Value, Note FROM Child;",
            ["CREATE INDEX IX_Child_ParentId ON MissingTable(ParentId);"]);

        await Assert.ThrowsAsync<SqliteException>(() => fixture.TableRebuilder.ExecuteAsync(plan));

        Assert.Equal(3, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM Child;"));
        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Child_ParentId';"));
        Assert.Equal(1, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name='TR_Child_Audit';"));
        Assert.Equal(0, await ScalarIntAsync(fixture.DatabasePath, "SELECT COUNT(*) FROM sqlite_master WHERE name IN ('Child_rebuild_new','Child__rebuild_old');"));
        Assert.Equal(0, await RowCountAsync(fixture.DatabasePath, "PRAGMA foreign_key_check;"));
        Assert.Equal("ok", await ScalarStringAsync(fixture.DatabasePath, "PRAGMA quick_check;"));
    }

    private static async Task CreateLegacyDatabaseAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await ExecuteAsync(path, "CREATE TABLE LegacyMarker(Id INTEGER PRIMARY KEY, Value TEXT NOT NULL); INSERT INTO LegacyMarker VALUES(1,'legacy-value'); PRAGMA user_version=0;");
    }

    private static async Task CreateRebuildFixtureAsync(string path) => await ExecuteAsync(path, """
        PRAGMA foreign_keys=ON;
        CREATE TABLE Parent(Id INTEGER PRIMARY KEY);
        CREATE TABLE Audit(ChildId INTEGER NOT NULL);
        CREATE TABLE Child(Id INTEGER PRIMARY KEY, ParentId INTEGER NOT NULL, Value INTEGER NOT NULL, Note TEXT NULL, FOREIGN KEY(ParentId) REFERENCES Parent(Id));
        CREATE INDEX IX_Child_ParentId ON Child(ParentId);
        CREATE TRIGGER TR_Child_Audit AFTER INSERT ON Child BEGIN INSERT INTO Audit(ChildId) VALUES (NEW.Id); END;
        INSERT INTO Parent VALUES(1);
        INSERT INTO Child VALUES(1,1,-2147483648,NULL),(2,1,0,''),(3,1,2147483647,'max');
        DELETE FROM Audit;
        """);

    private static async Task<string> CreateMigrationBackupAsync(DatabaseTestFixture fixture, Guid operationId)
    {
        var path = Path.Combine(fixture.Directory, "Backups", "Migration", $"WorkoutTracker_PreMigration_0_1_test_{operationId}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var source = Connection(fixture.DatabasePath, SqliteOpenMode.ReadOnly);
        await using var destination = Connection(path, SqliteOpenMode.ReadWriteCreate);
        await source.OpenAsync(); await destination.OpenAsync(); source.BackupDatabase(destination);
        return path;
    }

    private static async Task<string> WritePreparedManifestAsync(DatabaseTestFixture fixture, Guid operationId, string backup)
    {
        var directory = Path.Combine(fixture.Directory, "Maintenance"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"MigrationOperation_{operationId}.json");
        var value = new { OperationId = operationId, FromVersion = 0, ToVersion = 1, BackupPath = backup, StartedAtUtcTicks = DateTimeOffset.UtcNow.UtcTicks, State = "Prepared", MigrationHistory = Array.Empty<string>() };
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(value));
        return path;
    }

    private static async Task ExecuteAsync(string path, string sql)
    {
        SqliteConnection.ClearAllPools(); await using var connection = Connection(path, SqliteOpenMode.ReadWriteCreate); await connection.OpenAsync();
        await using var command = connection.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }
    private static async Task<int> ScalarIntAsync(string path, string sql) => Convert.ToInt32(await ScalarAsync(path, sql));
    private static async Task<long> ScalarLongAsync(string path, string sql) => Convert.ToInt64(await ScalarAsync(path, sql));
    private static async Task<string?> ScalarStringAsync(string path, string sql) => Convert.ToString(await ScalarAsync(path, sql));
    private static async Task<object?> ScalarAsync(string path, string sql) { SqliteConnection.ClearAllPools(); await using var connection = Connection(path, SqliteOpenMode.ReadOnly); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync(); }
    private static async Task<int> RowCountAsync(string path, string sql) { SqliteConnection.ClearAllPools(); await using var connection = Connection(path, SqliteOpenMode.ReadOnly); await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = sql; await using var reader = await command.ExecuteReaderAsync(); var count = 0; while (await reader.ReadAsync()) count++; return count; }
    private static SqliteConnection Connection(string path, SqliteOpenMode mode) => new(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, ForeignKeys = true, Pooling = false }.ToString());

    private sealed class MigrationInjector(params MigrationFailurePoint[] points) : IMigrationFailureInjector
    {
        private readonly HashSet<MigrationFailurePoint> _points = [.. points];
        public void ThrowIfRequested(MigrationFailurePoint point) { if (_points.Contains(point)) throw new IOException($"Injected {point}"); }
    }

    private sealed class RejectingProcessLock : IMigrationProcessLockProvider
    {
        public TimeSpan ReceivedTimeout { get; private set; }
        public IDisposable? TryAcquire(string databasePath, TimeSpan timeout) { ReceivedTimeout = timeout; return null; }
    }
}
