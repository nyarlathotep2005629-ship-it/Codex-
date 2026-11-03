using WorkoutTracker.Application;

namespace WorkoutTracker.Mobile;

internal sealed class WeeklySchedulePage : MobilePageBase
{
    private sealed record Choice(string Label, Guid? MenuId, bool IsRest);
    private readonly MobileWorkoutController _workouts;
    private readonly Dictionary<DayOfWeek, Picker> _basePickers = [];
    private Choice[] _choices = [];
    private DateOnly _date = DateOnly.FromDateTime(DateTime.Today);
    private DatePicker? _datePicker;
    private Picker? _overridePicker;
    private Label? _status;
    private bool _loading;

    public WeeklySchedulePage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "週間メニュー";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        Content = new VerticalStackLayout { Padding = 20, Children = { Text("週間メニュー", 28, bold: true), Text("読み込み中...", 14, Secondary) } };
        try
        {
            var menus = await _workouts.GetMasterMenusAsync();
            _choices = [new("未設定", null, false), new("休息日", null, true), .. menus.Select(x => new Choice(x.Name, x.Id, false))];
            var baseDays = await _workouts.GetBaseWeekAsync();
            var weekDays = await _workouts.GetPlannedWeekAsync(_date);
            Render(baseDays, weekDays);
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("予定を読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")), Action("再試行", async (_, _) => await LoadAsync()) } }; }
        finally { _loading = false; }
    }

    private void Render(IReadOnlyList<BaseDayDto> baseDays, IReadOnlyList<PlannedDayDto> weekDays)
    {
        var stack = new VerticalStackLayout { Padding = 16, Spacing = 14 };
        stack.Children.Add(Text("週間メニュー", 28, bold: true));
        stack.Children.Add(Text("予定は実際の記録と別です。基本メニューは毎週繰り返します。", 14, Secondary));

        _basePickers.Clear();
        var baseStack = new VerticalStackLayout { Spacing = 9 };
        baseStack.Children.Add(Text("基本の月曜〜日曜", 19, bold: true));
        foreach (var day in baseDays)
        {
            var picker = MakePicker();
            picker.SelectedIndex = ChoiceIndex(day.MenuId, day.IsRest);
            _basePickers[day.Day] = picker;
            baseStack.Children.Add(Text(DayLabel(day.Day), 14, Secondary));
            baseStack.Children.Add(picker);
        }
        baseStack.Children.Add(Action("基本メニューを保存", async (_, _) => await SaveBaseAsync(), primary: true));
        stack.Children.Add(Card(baseStack));

        var weekStack = new VerticalStackLayout { Spacing = 9 };
        weekStack.Children.Add(Text("指定した日・週の予定", 19, bold: true));
        _datePicker = new DatePicker { Date = _date.ToDateTime(TimeOnly.MinValue), TextColor = Primary, BackgroundColor = Color.FromArgb("#303030"), MinimumHeightRequest = 48 };
        weekStack.Children.Add(_datePicker);
        weekStack.Children.Add(Action("週を表示", async (_, _) => { _date = SelectedDate(); await LoadAsync(); }));
        foreach (var day in weekDays)
        {
            var source = day.Source switch { ScheduleSource.SelectedDate => "この日だけ", ScheduleSource.SelectedWeek => "この週だけ", ScheduleSource.BaseWeek => "基本", _ => "未設定" };
            var label = day.IsRest ? "休息日" : day.MenuName ?? "未設定";
            weekStack.Children.Add(Card(new VerticalStackLayout { Spacing = 4, Children = { Text($"{day.Date:MM/dd} {DayLabel(day.Date.DayOfWeek)}", 15, Accent, true), Text($"{label} ・ {day.PlannedSetCount}セット ・ {source}", 14, Secondary) } }));
        }
        weekStack.Children.Add(Text("対象日の変更内容", 15, bold: true));
        _overridePicker = MakePicker();
        var selected = weekDays.Single(x => x.Date == _date);
        _overridePicker.SelectedIndex = ChoiceIndex(selected.MenuId, selected.IsRest);
        weekStack.Children.Add(_overridePicker);
        weekStack.Children.Add(Action("この日だけ変更", async (_, _) => await ApplyAsync(false), primary: true));
        weekStack.Children.Add(Action("この週の同じ曜日を変更", async (_, _) => await ApplyAsync(true)));
        weekStack.Children.Add(Action("日付変更を解除", async (_, _) => await ClearAsync(false)));
        weekStack.Children.Add(Action("週変更を解除", async (_, _) => await ClearAsync(true)));
        weekStack.Children.Add(Action("対象日の予定から記録開始", async (_, _) => await StartPlannedAsync()));
        stack.Children.Add(Card(weekStack));
        _status = Text(string.Empty, 14, Accent);
        stack.Children.Add(_status);
        Content = new ScrollView { Content = stack };
    }

    private Picker MakePicker() => new() { Title = "メニューまたは休息日", ItemsSource = _choices.Select(x => x.Label).ToArray(), TextColor = Primary, TitleColor = Secondary, BackgroundColor = Color.FromArgb("#303030"), MinimumHeightRequest = 48 };
    private int ChoiceIndex(Guid? id, bool rest) => Math.Max(0, Array.FindIndex(_choices, x => x.MenuId == id && x.IsRest == rest));
    private DateOnly SelectedDate() => DateOnly.FromDateTime(_datePicker?.Date ?? DateTime.Today);

    private async Task SaveBaseAsync()
    {
        try
        {
            var days = _basePickers.Select(x => { var choice = _choices[x.Value.SelectedIndex]; return new BaseDayDto(x.Key, choice.MenuId, choice.IsRest); }).ToArray();
            await _workouts.SaveBaseWeekAsync(days); _date = SelectedDate(); await LoadAsync(); _status!.Text = "基本メニューを保存しました。";
        }
        catch { _status!.Text = "基本メニューを保存できませんでした。"; }
    }

    private async Task ApplyAsync(bool forWeek)
    {
        try
        {
            var choice = _choices[_overridePicker!.SelectedIndex];
            if (choice.MenuId is null && !choice.IsRest) { _status!.Text = "メニューまたは休息日を選択してください。"; return; }
            _date = SelectedDate();
            await _workouts.SetScheduleOverrideAsync(_date, choice.MenuId, choice.IsRest, forWeek);
            await LoadAsync(); _status!.Text = forWeek ? "この週の指定曜日だけ変更しました。" : "この日だけ変更しました。";
        }
        catch { _status!.Text = "予定を変更できませんでした。"; }
    }

    private async Task ClearAsync(bool forWeek)
    {
        try { _date = SelectedDate(); await _workouts.ClearScheduleOverrideAsync(_date, forWeek); await LoadAsync(); _status!.Text = "変更を解除しました。"; }
        catch { _status!.Text = "変更を解除できませんでした。"; }
    }

    private async Task StartPlannedAsync()
    {
        try
        {
            _date = SelectedDate();
            if (_date > DateOnly.FromDateTime(DateTime.Today)) { _status!.Text = "未来の予定は当日以降に開始できます。"; return; }
            if (_workouts.HasActiveWorkout) { _status!.Text = "進行中の記録を先に完了してください。"; return; }
            var day = (await _workouts.GetPlannedWeekAsync(_date)).Single(x => x.Date == _date);
            if (day.MenuId is null || day.IsRest) { _status!.Text = "この日に開始できる予定メニューはありません。"; return; }
            await _workouts.StartOnDateAsync(day.MenuId, _date);
            await Shell.Current.GoToAsync("//workout");
        }
        catch { _status!.Text = "予定メニューから記録を開始できませんでした。"; }
    }

    private static string DayLabel(DayOfWeek day) => day switch { DayOfWeek.Monday => "月曜日", DayOfWeek.Tuesday => "火曜日", DayOfWeek.Wednesday => "水曜日", DayOfWeek.Thursday => "木曜日", DayOfWeek.Friday => "金曜日", DayOfWeek.Saturday => "土曜日", _ => "日曜日" };
}
