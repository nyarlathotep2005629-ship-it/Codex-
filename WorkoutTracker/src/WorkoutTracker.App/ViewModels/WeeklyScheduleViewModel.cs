using System.Collections.ObjectModel;
using System.Windows.Input;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;

namespace WorkoutTracker.App.ViewModels;

public sealed record ScheduleChoice(string Label, Guid? MenuId, bool IsRest)
{
    public bool IsUnset => MenuId is null && !IsRest;
}

public sealed class BaseScheduleRow(DayOfWeek day, ScheduleChoice choice) : ObservableObject
{
    private ScheduleChoice _selectedChoice = choice;
    public DayOfWeek Day { get; } = day;
    public string DayLabel => Day switch { DayOfWeek.Monday => "月曜日", DayOfWeek.Tuesday => "火曜日", DayOfWeek.Wednesday => "水曜日", DayOfWeek.Thursday => "木曜日", DayOfWeek.Friday => "金曜日", DayOfWeek.Saturday => "土曜日", _ => "日曜日" };
    public ScheduleChoice SelectedChoice { get => _selectedChoice; set => SetProperty(ref _selectedChoice, value); }
}

public sealed class PlannedScheduleRow(PlannedDayDto day)
{
    public DateOnly Date => day.Date;
    public string DateLabel => $"{day.Date:MM/dd} ({new BaseScheduleRow(day.Date.DayOfWeek, new("", null, false)).DayLabel[..1]})";
    public string PlanLabel => day.IsRest ? "休息日" : day.MenuName ?? "未設定";
    public string SourceLabel => day.Source switch { ScheduleSource.SelectedDate => "この日だけ", ScheduleSource.SelectedWeek => "この週だけ", ScheduleSource.BaseWeek => "基本", _ => "未設定" };
    public int PlannedSetCount => day.PlannedSetCount;
    public Guid? MenuId => day.MenuId;
    public bool IsRest => day.IsRest;
}

public sealed class WeeklyScheduleViewModel : ObservableObject
{
    private readonly IWeeklyScheduleService _schedule;
    private readonly MasterDataService _masterData;
    private readonly ActiveWorkoutViewModel _workout;
    private readonly IUiExceptionReporter _errors;
    private DateTime? _selectedDate = DateTime.Today;
    private ScheduleChoice? _selectedChoice;
    private string? _status;
    private bool _isBusy;

    public WeeklyScheduleViewModel(IWeeklyScheduleService schedule, MasterDataService masterData, ActiveWorkoutViewModel workout, IUiExceptionReporter? errors = null)
    {
        _schedule = schedule; _masterData = masterData; _workout = workout; _errors = errors ?? NullUiExceptionReporter.Instance;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        SaveBaseCommand = new AsyncRelayCommand(SaveBaseAsync);
        ApplyDateCommand = new AsyncRelayCommand(() => ApplyOverrideAsync(false));
        ApplyWeekCommand = new AsyncRelayCommand(() => ApplyOverrideAsync(true));
        ClearDateCommand = new AsyncRelayCommand(() => ClearOverrideAsync(false));
        ClearWeekCommand = new AsyncRelayCommand(() => ClearOverrideAsync(true));
        StartPlannedCommand = new AsyncRelayCommand(StartPlannedAsync);
    }

    public ObservableCollection<ScheduleChoice> Choices { get; } = [];
    public ObservableCollection<BaseScheduleRow> BaseRows { get; } = [];
    public ObservableCollection<PlannedScheduleRow> WeekRows { get; } = [];
    public DateTime? SelectedDate { get => _selectedDate; set => SetProperty(ref _selectedDate, value); }
    public ScheduleChoice? SelectedChoice { get => _selectedChoice; set => SetProperty(ref _selectedChoice, value); }
    public string? Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public ICommand RefreshCommand { get; }
    public ICommand SaveBaseCommand { get; }
    public ICommand ApplyDateCommand { get; }
    public ICommand ApplyWeekCommand { get; }
    public ICommand ClearDateCommand { get; }
    public ICommand ClearWeekCommand { get; }
    public ICommand StartPlannedCommand { get; }
    public event EventHandler? WorkoutStarted;

    public Task InitializeAsync() => RefreshAsync();

    public async Task RefreshAsync() => await RunAsync(async () =>
    {
        var menus = await _masterData.GetMenusAsync();
        Choices.Clear(); Choices.Add(new("未設定", null, false)); Choices.Add(new("休息日", null, true));
        foreach (var menu in menus) Choices.Add(new(menu.Name, menu.Id, false));
        var baseDays = await _schedule.GetBaseWeekAsync();
        BaseRows.Clear();
        foreach (var day in baseDays) BaseRows.Add(new(day.Day, FindChoice(day.MenuId, day.IsRest)));
        await LoadWeekCoreAsync();
    });

    private async Task LoadWeekCoreAsync()
    {
        var date = DateOnly.FromDateTime(SelectedDate ?? DateTime.Today);
        var days = await _schedule.GetWeekAsync(date);
        WeekRows.Clear();
        foreach (var day in days) WeekRows.Add(new(day));
        var selected = days.Single(x => x.Date == date);
        SelectedChoice = FindChoice(selected.MenuId, selected.IsRest);
    }

    private ScheduleChoice FindChoice(Guid? menuId, bool isRest) => Choices.FirstOrDefault(x => x.MenuId == menuId && x.IsRest == isRest) ?? Choices[0];

    private async Task SaveBaseAsync() => await RunAsync(async () =>
    {
        await _schedule.SaveBaseWeekAsync(BaseRows.Select(x => new BaseDayDto(x.Day, x.SelectedChoice.MenuId, x.SelectedChoice.IsRest)).ToArray());
        await LoadWeekCoreAsync(); Status = "基本の週間メニューを保存しました。";
    });

    private async Task ApplyOverrideAsync(bool week) => await RunAsync(async () =>
    {
        var date = DateOnly.FromDateTime(SelectedDate ?? DateTime.Today);
        var choice = SelectedChoice;
        if (choice is null || choice.IsUnset) { Status = "メニューまたは休息日を選択してください。"; return; }
        if (week) await _schedule.SetWeekDayAsync(date, choice.MenuId, choice.IsRest);
        else await _schedule.SetDateAsync(date, choice.MenuId, choice.IsRest);
        await LoadWeekCoreAsync(); Status = week ? "この週の指定曜日だけ変更しました。" : "この日だけ変更しました。";
    });

    private async Task ClearOverrideAsync(bool week) => await RunAsync(async () =>
    {
        var date = DateOnly.FromDateTime(SelectedDate ?? DateTime.Today);
        if (week) await _schedule.ClearWeekDayAsync(date); else await _schedule.ClearDateAsync(date);
        await LoadWeekCoreAsync(); Status = "変更を解除し、元の予定を表示しました。";
    });

    private async Task StartPlannedAsync() => await RunAsync(async () =>
    {
        var date = DateOnly.FromDateTime(SelectedDate ?? DateTime.Today);
        if (date > DateOnly.FromDateTime(DateTime.Today)) { Status = "未来の予定は当日以降に開始できます。"; return; }
        var planned = (await _schedule.GetWeekAsync(date)).Single(x => x.Date == date);
        if (planned.MenuId is null || planned.IsRest) { Status = "この日に開始できる予定メニューはありません。"; return; }
        if (_workout.IsActive) { Status = "進行中のワークアウトを先に完了または破棄してください。"; return; }
        var result = await _workout.StartAsync(planned.MenuId, date);
        if (result is StartWorkoutResult.Started) WorkoutStarted?.Invoke(this, EventArgs.Empty);
        else Status = "進行中の記録を先に確認してください。";
    });

    private async Task RunAsync(Func<Task> action)
    {
        if (IsBusy) return;
        IsBusy = true; Status = null;
        try { await action(); }
        catch (Exception ex) { _errors.Report(ex, "WeeklySchedule.Operation"); Status = "予定を操作できませんでした。内容を確認して再試行してください。"; }
        finally { IsBusy = false; }
    }
}
