using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

public sealed class HistoryCalendarQuery(IDbContextFactory<WorkoutTrackerDbContext> factory) : IHistoryCalendarQuery
{
    public async Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken ct = default)
    {
        if (query.PageSize is not (25 or 50 or 100)) throw new ArgumentOutOfRangeException(nameof(query.PageSize), "Page size must be 25, 50, or 100.");
        if (query.From.HasValue && query.To.HasValue && query.From >= query.To) throw new ArgumentException("Period must be a non-empty half-open interval.", nameof(query));
        await using var db = await factory.CreateDbContextAsync(ct);
        var sessions = ApplyFilters(db.WorkoutSessions.AsNoTracking().Where(x => x.Status == WorkoutStatus.Completed), query);
        if (query.Cursor is { } cursor)
            sessions = sessions.Where(x => x.WorkoutDate < cursor.WorkoutDate || x.WorkoutDate == cursor.WorkoutDate && (x.CompletedAtUtc < cursor.CompletedAtUtc || x.CompletedAtUtc == cursor.CompletedAtUtc && x.Id.CompareTo(cursor.Id) < 0));
        var pageRows = await sessions.OrderByDescending(x => x.WorkoutDate).ThenByDescending(x => x.CompletedAtUtc).ThenByDescending(x => x.Id)
            .Take(query.PageSize + 1)
            .Select(x => new PageRow(x.Id, x.WorkoutDate, x.CompletedAtUtc!.Value, x.Name, x.SourceMenuId, x.SourceMenu == null ? null : x.SourceMenu.Name))
            .ToListAsync(ct);
        HistoryCursor? next = null;
        if (pageRows.Count > query.PageSize) { pageRows.RemoveAt(pageRows.Count - 1); var last = pageRows[^1]; next = new(last.WorkoutDate, last.CompletedAtUtc, last.Id); }
        var ids = pageRows.Select(x => x.Id).ToList();
        var setRows = await db.WorkoutSets.AsNoTracking().Where(x => ids.Contains(x.WorkoutExercise.WorkoutSessionId))
            .Select(x => new SetSummaryRow(x.WorkoutExercise.WorkoutSessionId, x.WorkoutExerciseId, x.WeightKg, x.Reps)).ToListAsync(ct);
        var items = pageRows.Select(x =>
        {
            var sets = setRows.Where(s => s.SessionId == x.Id).ToList();
            return new WorkoutHistoryItem(x.Id, x.WorkoutDate, x.CompletedAtUtc, x.Name, x.SourceMenuId, x.SourceMenuName,
                sets.Where(s => s.WeightKg.HasValue && s.Reps.HasValue).Sum(s => s.WeightKg!.Value * s.Reps!.Value),
                sets.Count(s => s.WeightKg.HasValue && s.Reps.HasValue), sets.Select(s => s.WorkoutExerciseId).Distinct().Count());
        }).ToList();
        return new(items, next);
    }

    public async Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var exercises = await db.Exercises.AsNoTracking()
            .Where(x => db.WorkoutExercises.Any(w => w.ExerciseId == x.Id && w.WorkoutSession.Status == WorkoutStatus.Completed))
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new HistoryFilterOption(x.Id, x.Name))
            .ToListAsync(ct);
        var muscleRows = await db.WorkoutExercises.AsNoTracking()
            .Where(x => x.WorkoutSession.Status == WorkoutStatus.Completed)
            .Select(x => new { Id = x.PrimaryMuscleIdSnapshot, Name = x.PrimaryMuscleNameSnapshot })
            .Distinct()
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .ToListAsync(ct);
        var muscles = muscleRows.Select(x => new HistoryFilterOption(x.Id, x.Name)).ToList();
        var menus = await db.WorkoutMenus.AsNoTracking()
            .Where(x => db.WorkoutSessions.Any(s => s.Status == WorkoutStatus.Completed && s.SourceMenuId == x.Id))
            .OrderBy(x => x.Name).ThenBy(x => x.Id)
            .Select(x => new HistoryFilterOption(x.Id, x.Name))
            .ToListAsync(ct);
        return new(exercises, muscles, menus);
    }

    public async Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var session = await db.WorkoutSessions.AsNoTracking().Where(x => x.Id == sessionId && x.Status == WorkoutStatus.Completed)
            .Include(x => x.SourceMenu).Include(x => x.Exercises.OrderBy(e => e.DisplayOrder)).ThenInclude(x => x.Sets.OrderBy(s => s.SetNumber)).SingleOrDefaultAsync(ct);
        if (session is null) return null;
        return new(session.Id, session.WorkoutDate, session.CompletedAtUtc!.Value, session.Name, session.SourceMenu?.Name, session.Note,
            session.Exercises.OrderBy(x => x.DisplayOrder).Select(x => new WorkoutDetailExerciseDto(x.Id, x.ExerciseId, x.ExerciseNameSnapshot, x.PrimaryMuscleIdSnapshot, x.PrimaryMuscleNameSnapshot, x.DisplayOrder, x.Note,
                x.Sets.OrderBy(s => s.SetNumber).Select(s => new WorkoutDetailSetDto(s.Id, s.SetNumber, s.WeightKg, s.Reps, s.Rpe)).ToList())).ToList());
    }

    public async Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken ct = default)
    {
        var start = new DateOnly(year, month, 1); var end = start.AddMonths(1);
        await using var db = await factory.CreateDbContextAsync(ct);
        var flatRows = await (from session in db.WorkoutSessions.AsNoTracking()
                              where session.Status == WorkoutStatus.Completed && session.WorkoutDate >= start && session.WorkoutDate < end
                              from exercise in session.Exercises.DefaultIfEmpty()
                              from set in exercise.Sets.DefaultIfEmpty()
                              orderby session.WorkoutDate, session.CompletedAtUtc descending, session.Id descending, exercise.DisplayOrder, set.SetNumber
                              select new CalendarFlatRow(session.Id, session.WorkoutDate, session.Name, session.SourceMenu == null ? null : session.SourceMenu.Name,
                                  exercise == null ? null : exercise.Id, exercise == null ? null : exercise.PrimaryMuscleNameSnapshot,
                                  set == null ? null : set.WeightKg, set == null ? null : set.Reps)).ToListAsync(ct);
        var sessions = flatRows.GroupBy(x => x.SessionId).Select(group => group.First()).ToList();
        var byDate = sessions.GroupBy(x => x.WorkoutDate).ToDictionary(x => x.Key, x => x.Select(session =>
        {
            var rows = flatRows.Where(r => r.SessionId == session.SessionId).ToList();
            return new CalendarWorkoutSummaryDto(session.SessionId, session.Name, session.SourceMenuName, rows.Where(r => r.PrimaryMuscleName is not null).Select(r => r.PrimaryMuscleName!).Distinct().ToList(),
                rows.Where(r => r.WeightKg.HasValue && r.Reps.HasValue).Sum(r => r.WeightKg!.Value * r.Reps!.Value), rows.Count(r => r.WeightKg.HasValue && r.Reps.HasValue));
        }).ToList());
        return new(year, month, Enumerable.Range(0, end.DayNumber - start.DayNumber).Select(offset => { var date = start.AddDays(offset); return new CalendarDayDto(date, byDate.GetValueOrDefault(date) ?? []); }).ToList());
    }

    private static IQueryable<WorkoutSession> ApplyFilters(IQueryable<WorkoutSession> query, HistoryQuery filter)
    {
        if (filter.From.HasValue) query = query.Where(x => x.WorkoutDate >= filter.From.Value);
        if (filter.To.HasValue) query = query.Where(x => x.WorkoutDate < filter.To.Value);
        if (filter.SourceMenuId.HasValue) query = query.Where(x => x.SourceMenuId == filter.SourceMenuId);
        if (filter.ExerciseId.HasValue) query = query.Where(x => x.Exercises.Any(e => e.ExerciseId == filter.ExerciseId));
        if (filter.PrimaryMuscleId.HasValue) query = query.Where(x => x.Exercises.Any(e => e.PrimaryMuscleIdSnapshot == filter.PrimaryMuscleId));
        return query;
    }

    private sealed record PageRow(Guid Id, DateOnly WorkoutDate, DateTimeOffset CompletedAtUtc, string? Name, Guid? SourceMenuId, string? SourceMenuName);
    private sealed record SetSummaryRow(Guid SessionId, Guid WorkoutExerciseId, decimal? WeightKg, int? Reps);
    private sealed record CalendarFlatRow(Guid SessionId, DateOnly WorkoutDate, string? Name, string? SourceMenuName, Guid? WorkoutExerciseId, string? PrimaryMuscleName, decimal? WeightKg, int? Reps);
}
