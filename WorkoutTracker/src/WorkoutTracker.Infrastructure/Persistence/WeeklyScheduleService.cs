using Microsoft.EntityFrameworkCore;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Infrastructure.Persistence;

internal sealed class WeeklyScheduleService(IDbContextFactory<WorkoutTrackerDbContext> factory) : IWeeklyScheduleService
{
    private sealed record Rule(int Kind, int Anchor, int Weekday, Guid? MenuId, bool IsRest);

    public async Task<IReadOnlyList<BaseDayDto>> GetBaseWeekAsync(CancellationToken cancellationToken = default)
    {
        var rules = await ReadRulesAsync(cancellationToken);
        return Enumerable.Range(0, 7).Select(i =>
        {
            var day = (DayOfWeek)((i + 1) % 7);
            var rule = rules.SingleOrDefault(x => x.Kind == 0 && x.Weekday == (int)day);
            return new BaseDayDto(day, rule?.MenuId, rule?.IsRest ?? false);
        }).ToArray();
    }

    public async Task<IReadOnlyList<PlannedDayDto>> GetWeekAsync(DateOnly dateInWeek, CancellationToken cancellationToken = default)
    {
        var monday = dateInWeek.AddDays(-(((int)dateInWeek.DayOfWeek + 6) % 7));
        var rules = await ReadRulesAsync(cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var menus = await db.WorkoutMenus.AsNoTracking().Include(x => x.Exercises).ToDictionaryAsync(x => x.Id, cancellationToken);
        return Enumerable.Range(0, 7).Select(offset =>
        {
            var date = monday.AddDays(offset);
            var weekday = (int)date.DayOfWeek;
            var rule = rules.SingleOrDefault(x => x.Kind == 2 && x.Anchor == date.DayNumber)
                ?? rules.SingleOrDefault(x => x.Kind == 1 && x.Anchor == monday.DayNumber && x.Weekday == weekday)
                ?? rules.SingleOrDefault(x => x.Kind == 0 && x.Weekday == weekday);
            var source = rule?.Kind switch { 2 => ScheduleSource.SelectedDate, 1 => ScheduleSource.SelectedWeek, 0 => ScheduleSource.BaseWeek, _ => ScheduleSource.None };
            var menu = rule?.MenuId is Guid id && menus.TryGetValue(id, out var found) ? found : null;
            return new PlannedDayDto(date, rule?.MenuId, menu?.Name, menu?.Exercises.Sum(x => x.DefaultSetCount) ?? 0, rule?.IsRest ?? false, source);
        }).ToArray();
    }

    public async Task SaveBaseWeekAsync(IReadOnlyList<BaseDayDto> days, CancellationToken cancellationToken = default)
    {
        if (days.Count != 7 || days.Select(x => x.Day).Distinct().Count() != 7 || days.Any(x => !Enum.IsDefined(x.Day) || (x.MenuId is not null && x.IsRest)))
            throw new DomainValidationException("基本メニューは月曜から日曜までを一度ずつ指定してください。");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var menuIds = days.Where(x => x.MenuId is not null).Select(x => x.MenuId!.Value).Distinct().ToArray();
        if (await db.WorkoutMenus.CountAsync(x => menuIds.Contains(x.Id), cancellationToken) != menuIds.Length)
            throw new KeyNotFoundException("予定のメニューが見つかりません。");
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("DELETE FROM WeeklyScheduleRules WHERE Kind=0;", cancellationToken);
        foreach (var day in days.Where(x => x.IsRest || x.MenuId is not null))
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO WeeklyScheduleRules (Kind, AnchorDayNumber, Weekday, MenuId, IsRest) VALUES (0, 0, {(int)day.Day}, {day.MenuId}, {(day.IsRest ? 1 : 0)});", cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public Task SetBaseDayAsync(DayOfWeek day, Guid? menuId, bool isRest, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(day)) throw new DomainValidationException("曜日が不正です。");
        return SetRuleAsync(0, 0, (int)day, menuId, isRest, cancellationToken);
    }

    public Task ClearBaseDayAsync(DayOfWeek day, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(day)) throw new DomainValidationException("曜日が不正です。");
        return DeleteRuleAsync(0, 0, (int)day, cancellationToken);
    }

    public Task SetWeekDayAsync(DateOnly date, Guid? menuId, bool isRest, CancellationToken cancellationToken = default)
    {
        ValidateDate(date);
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return SetRuleAsync(1, monday.DayNumber, (int)date.DayOfWeek, menuId, isRest, cancellationToken);
    }

    public Task SetDateAsync(DateOnly date, Guid? menuId, bool isRest, CancellationToken cancellationToken = default)
    {
        ValidateDate(date);
        return SetRuleAsync(2, date.DayNumber, (int)date.DayOfWeek, menuId, isRest, cancellationToken);
    }

    public Task ClearWeekDayAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        ValidateDate(date);
        var monday = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return DeleteRuleAsync(1, monday.DayNumber, (int)date.DayOfWeek, cancellationToken);
    }

    public Task ClearDateAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        ValidateDate(date);
        return DeleteRuleAsync(2, date.DayNumber, (int)date.DayOfWeek, cancellationToken);
    }

    private async Task SetRuleAsync(int kind, int anchor, int weekday, Guid? menuId, bool isRest, CancellationToken cancellationToken)
    {
        if ((menuId is null && !isRest) || (menuId is not null && isRest)) throw new DomainValidationException("メニューまたは休息日を選択してください。");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        if (menuId is Guid id && !await db.WorkoutMenus.AnyAsync(x => x.Id == id, cancellationToken)) throw new KeyNotFoundException("予定のメニューが見つかりません。");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO WeeklyScheduleRules (Kind, AnchorDayNumber, Weekday, MenuId, IsRest)
            VALUES ({kind}, {anchor}, {weekday}, {menuId}, {(isRest ? 1 : 0)})
            ON CONFLICT(Kind, AnchorDayNumber, Weekday) DO UPDATE SET MenuId=excluded.MenuId, IsRest=excluded.IsRest;
            """, cancellationToken);
    }

    private async Task DeleteRuleAsync(int kind, int anchor, int weekday, CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM WeeklyScheduleRules WHERE Kind={kind} AND AnchorDayNumber={anchor} AND Weekday={weekday};", cancellationToken);
    }

    private async Task<IReadOnlyList<Rule>> ReadRulesAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = db.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT Kind, AnchorDayNumber, Weekday, MenuId, IsRest FROM WeeklyScheduleRules;";
            var result = new List<Rule>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                result.Add(new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.IsDBNull(3) ? null : Guid.Parse(reader.GetString(3)), reader.GetInt32(4) != 0));
            return result;
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private static void ValidateDate(DateOnly date)
    {
        if (date < new DateOnly(1900, 1, 1) || date > new DateOnly(2100, 12, 31)) throw new DomainValidationException("予定日は1900年から2100年までです。");
    }
}
