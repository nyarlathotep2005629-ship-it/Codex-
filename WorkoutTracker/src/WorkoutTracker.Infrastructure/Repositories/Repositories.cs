using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure.Persistence;

namespace WorkoutTracker.Infrastructure.Repositories;

internal sealed class WorkoutSessionRepository(IDbContextFactory<WorkoutTrackerDbContext> factory, IWriteContextAccessor writeContext) : IWorkoutSessionRepository
{
    public async Task<WorkoutSession?> GetDraftAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.WorkoutSessions.AsNoTracking().SingleOrDefaultAsync(x => x.Status == WorkoutStatus.Draft, ct); }
    public async Task<WorkoutSession?> GetByIdAsync(Guid id, CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.WorkoutSessions.AsNoTracking().Include(x => x.Exercises).ThenInclude(x => x.Sets).SingleOrDefaultAsync(x => x.Id == id, ct); }
    public Task AddAsync(WorkoutSession session, CancellationToken ct = default) { writeContext.Current.WorkoutSessions.Add(session); return Task.CompletedTask; }
    public async Task DeleteDraftAsync(Guid id, CancellationToken ct = default) { var db = writeContext.Current; var draft = await db.WorkoutSessions.SingleOrDefaultAsync(x => x.Id == id && x.Status == WorkoutStatus.Draft, ct); if (draft is not null) db.Remove(draft); }
    public async Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken ct = default)
    {
        if (query.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(query));
        await using var db = await factory.CreateDbContextAsync(ct);
        var q = db.WorkoutSessions.AsNoTracking().Where(x => x.Status == WorkoutStatus.Completed);
        if (query.From.HasValue) q = q.Where(x => x.WorkoutDate >= query.From.Value);
        if (query.To.HasValue) q = q.Where(x => x.WorkoutDate < query.To.Value);
        if (query.SourceMenuId.HasValue) q = q.Where(x => x.SourceMenuId == query.SourceMenuId);
        if (query.ExerciseId.HasValue) q = q.Where(x => x.Exercises.Any(e => e.ExerciseId == query.ExerciseId));
        if (query.PrimaryMuscleId.HasValue) q = q.Where(x => x.Exercises.Any(e => e.PrimaryMuscleIdSnapshot == query.PrimaryMuscleId));
        if (query.Cursor is not null)
        {
            var c = query.Cursor;
            q = q.Where(x => x.WorkoutDate < c.WorkoutDate || x.WorkoutDate == c.WorkoutDate && (x.CompletedAtUtc < c.CompletedAtUtc || x.CompletedAtUtc == c.CompletedAtUtc && x.Id.CompareTo(c.Id) < 0));
        }
        var rows = await q.OrderByDescending(x => x.WorkoutDate).ThenByDescending(x => x.CompletedAtUtc).ThenByDescending(x => x.Id).Take(query.PageSize + 1).Select(x => new WorkoutHistoryItem(x.Id, x.WorkoutDate, x.CompletedAtUtc!.Value, x.Name, x.SourceMenuId)).ToListAsync(ct);
        HistoryCursor? next = null;
        if (rows.Count > query.PageSize) { rows.RemoveAt(rows.Count - 1); var last = rows[^1]; next = new(last.WorkoutDate, last.CompletedAtUtc, last.Id); }
        return new(rows, next);
    }
    public async Task<IReadOnlyList<WorkoutSession>> GetCalendarMonthAsync(int year, int month, CancellationToken ct = default)
    {
        var start = new DateOnly(year, month, 1); var end = start.AddMonths(1);
        await using var db = await factory.CreateDbContextAsync(ct);
        return await db.WorkoutSessions.AsNoTracking().Where(x => x.Status == WorkoutStatus.Completed && x.WorkoutDate >= start && x.WorkoutDate < end).OrderBy(x => x.WorkoutDate).ToListAsync(ct);
    }
}

internal sealed class ExerciseRepository(IDbContextFactory<WorkoutTrackerDbContext> factory) : IExerciseRepository
{
    public async Task<IReadOnlyList<ExerciseSummary>> GetActiveAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.Exercises.AsNoTracking().Where(x => x.IsActive).OrderBy(x => x.Name).Select(x => new ExerciseSummary(x.Id, x.Name, x.PrimaryMuscleId, x.PrimaryMuscle.Name)).ToListAsync(ct); }
    public async Task<Exercise?> GetByIdAsync(Guid id, CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.Exercises.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct); }
    public async Task<IReadOnlyList<ExerciseSummary>> SearchAsync(string text, CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); var term = text.Trim(); return await db.Exercises.AsNoTracking().Where(x => x.IsActive && EF.Functions.Like(x.Name, $"%{term}%")).OrderBy(x => x.Name).Select(x => new ExerciseSummary(x.Id, x.Name, x.PrimaryMuscleId, x.PrimaryMuscle.Name)).ToListAsync(ct); }
}

internal sealed class WorkoutMenuRepository(IDbContextFactory<WorkoutTrackerDbContext> factory) : IWorkoutMenuRepository
{
    public async Task<IReadOnlyList<WorkoutMenuSummary>> GetSummariesAsync(CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.WorkoutMenus.AsNoTracking().OrderBy(x => x.Name).Select(x => new WorkoutMenuSummary(x.Id, x.Name, x.Exercises.Count)).ToListAsync(ct); }
    public async Task<WorkoutMenu?> GetWithExercisesAsync(Guid id, CancellationToken ct = default) { await using var db = await factory.CreateDbContextAsync(ct); return await db.WorkoutMenus.AsNoTracking().Include(x => x.Exercises.OrderBy(e => e.DisplayOrder)).ThenInclude(x => x.Exercise).SingleOrDefaultAsync(x => x.Id == id, ct); }
}
