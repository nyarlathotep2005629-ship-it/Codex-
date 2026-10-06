using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace WorkoutTracker.Infrastructure.Persistence;

public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<WorkoutTrackerDbContext>
{
    public WorkoutTrackerDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<WorkoutTrackerDbContext>()
            .UseSqlite(SqliteConnectionStringFactory.Create(Path.Combine(AppContext.BaseDirectory, "design-time.db")))
            .Options;
        return new WorkoutTrackerDbContext(options);
    }
}

