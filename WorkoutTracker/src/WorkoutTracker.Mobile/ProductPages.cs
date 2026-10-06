using System.Globalization;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls.Shapes;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Mobile;

internal abstract class MobilePageBase : ContentPage
{
    protected static readonly Color PageBackground = Color.FromArgb("#121212");
    protected static readonly Color Surface = Color.FromArgb("#1E1E1E");
    protected static readonly Color Primary = Color.FromArgb("#FFFFFF");
    protected static readonly Color Secondary = Color.FromArgb("#BDBDBD");
    protected static readonly Color Accent = Color.FromArgb("#80CBC4");

    protected static Button Action(string text, EventHandler clicked, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            MinimumHeightRequest = 48,
            BackgroundColor = primary ? Accent : Color.FromArgb("#303030"),
            TextColor = primary ? Color.FromArgb("#10201E") : Primary,
            FontAttributes = FontAttributes.Bold
        };
        button.Clicked += clicked;
        return button;
    }

    protected static Button DangerAction(string text, EventHandler clicked)
    {
        var button = Action(text, clicked);
        button.BackgroundColor = Color.FromArgb("#2A1B1B");
        button.TextColor = Color.FromArgb("#EF9A9A");
        return button;
    }

    protected static Label Text(string value, double size = 14, Color? color = null, bool bold = false) => new()
    {
        Text = value,
        TextColor = color ?? Primary,
        FontSize = size,
        FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None,
        LineBreakMode = LineBreakMode.WordWrap
    };

    protected static Border Card(View content) => new()
    {
        BackgroundColor = Surface,
        Padding = 16,
        StrokeThickness = 0,
        StrokeShape = new RoundRectangle { CornerRadius = 12 },
        Content = content
    };
}

internal sealed class HomePage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;
    private bool _refreshing;
    private DashboardDto? _dashboard;

    public HomePage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "Home";
        BackgroundColor = PageBackground;
        Loaded += async (_, _) => await RefreshAsync();
        Render();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            await _workouts.RefreshHomeAsync();
            _dashboard = await _workouts.GetDashboardAsync(DashboardPeriod.ThreeMonths, DateOnly.FromDateTime(DateTime.Today), []);
            Render();
        }
        catch { Content = ErrorContent("ローカルデータベースを起動できませんでした。再起動して再試行してください。"); }
        finally { _refreshing = false; }
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 14 };
        stack.Children.Add(Text("ホーム", 28, bold: true));
        stack.Children.Add(Text("この端末内に保存されます。ネットワーク接続は必要ありません。", 14, Secondary));
        stack.Children.Add(Action("ワークアウトを開始", OnStartClicked, primary: true));
        stack.Children.Add(Action("統計を開く", async (_, _) => await Shell.Current.GoToAsync("//statistics")));
        if (_workouts.HasActiveWorkout)
        {
            stack.Children.Add(Card(new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Text("保存済みのワークアウト", 18, bold: true),
                    Text(_workouts.CurrentWorkout?.WorkoutName ?? "進行中のワークアウト", 14, Secondary),
                    Action("再開する", OnResumeClicked)
                }
            }));
        }

        stack.Children.Add(Text("直近3か月のダッシュボード", 18, bold: true));
        if (_dashboard is not null)
        {
            stack.Children.Add(BuildDashboardCard(_dashboard));
        }

        stack.Children.Add(Text("最近完了したワークアウト", 18, bold: true));
        if (_dashboard is null || _dashboard.RecentWorkouts.Count == 0)
        {
            stack.Children.Add(Card(Text("まだ完了したワークアウトはありません。これは正常な空の状態です。", 14, Secondary)));
        }
        else
        {
            foreach (var item in _dashboard.RecentWorkouts)
            {
                stack.Children.Add(Card(new VerticalStackLayout
                {
                    Spacing = 4,
                    Children =
                    {
                        Text(item.Name ?? "ワークアウト", 16, bold: true),
                        Text($"{item.Date:yyyy/MM/dd} ・ {item.Sets} セット ・ {item.Volume:0.##} kg", 13, Secondary),
                        Action("詳細を開く", async (_, _) => await Navigation.PushAsync(new WorkoutDetailPage(_workouts, item.WorkoutSessionId)))
                    }
                }));
            }
        }

        Content = new ScrollView { Content = stack };
    }

    private async void OnStartClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//workout");
    private async void OnResumeClicked(object? sender, EventArgs e) => await Shell.Current.GoToAsync("//workout");
    private static View BuildDashboardCard(DashboardDto dashboard)
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(Text($"{dashboard.Summary.WorkoutCount} 回 ・ {dashboard.Summary.TotalSets} セット ・ {dashboard.Summary.TotalVolume:0.##} kg", 17, Accent, true));
        var recentFrequency = dashboard.Frequency.TakeLast(4).ToArray();
        if (recentFrequency.Length > 0)
            stack.Children.Add(Text("運動頻度: " + string.Join("  ", recentFrequency.Select(x => $"{x.BucketStart:M/d} {x.WorkoutCount}回")), 13, Secondary));
        if (dashboard.MuscleVolumes.Count > 0)
            stack.Children.Add(Text("部位別: " + string.Join(" / ", dashboard.MuscleVolumes.Take(3).Select(x => $"{x.Name} {x.Volume:0.#}kg")), 13, Secondary));
        return Card(stack);
    }
    private static View ErrorContent(string message) => new VerticalStackLayout { Padding = 20, Children = { Text(message, 16, Color.FromArgb("#EF9A9A")) } };
}

internal sealed class WorkoutPage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;
    private Label? _saveState;
    private Label? _error;
    private Label? _restTimerState;
    private bool _timerTicking;

    public WorkoutPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "Workout";
        BackgroundColor = PageBackground;
        _workouts.Changed += (_, _) => MainThread.BeginInvokeOnMainThread(UpdateStateLabels);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await _workouts.EnsureStartedAsync();
            await _workouts.RefreshCatalogAsync();
            if (_workouts.HasActiveWorkout) await _workouts.ResumeAsync();
            Render();
            EnsureRestTimerTicking();
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("ワークアウトを読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }

    private void Render()
    {
        Content = new ScrollView { Content = _workouts.HasActiveWorkout ? ActiveContent() : StartContent() };
        UpdateStateLabels();
    }

    private View StartContent()
    {
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 14 };
        stack.Children.Add(Text("ワークアウトを開始", 28, bold: true));
        stack.Children.Add(Text("メニューを選ぶか、空のワークアウトを開始できます。", 14, Secondary));
        stack.Children.Add(Action("空のワークアウトを開始", OnStartEmptyClicked, primary: true));
        stack.Children.Add(Text("メニューから開始", 18, bold: true));
        if (_workouts.Menus.Count == 0)
        {
            stack.Children.Add(Card(new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    Text("利用可能なメニューはまだありません。空のワークアウトは開始できます。", 14, Secondary),
                    Action("トレーニングデータを開く", async (_, _) => await Shell.Current.GoToAsync("//more"))
                }
            }));
        }
        else
        {
            foreach (var menu in _workouts.Menus)
            {
                var button = Action($"{menu.Name}（{menu.ExerciseCount}種目）", async (_, _) => await StartMenuAsync(menu.Id));
                stack.Children.Add(button);
            }
        }
        return stack;
    }

    private View ActiveContent()
    {
        var workout = _workouts.CurrentWorkout!;
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 14 };
        stack.Children.Add(Text(workout.WorkoutName ?? "ワークアウト", 28, bold: true));
        _saveState = Text(_workouts.SaveStateText, 14, Accent);
        _error = Text(_workouts.LastError ?? string.Empty, 14, Color.FromArgb("#EF9A9A"));
        stack.Children.Add(_saveState);
        stack.Children.Add(_error);
        stack.Children.Add(Action("メニューから種目を追加", OnAddMenuClicked));
        stack.Children.Add(Action("＋ 種目を追加", OnAddExerciseClicked));
        if (workout.Exercises.Count == 0)
        {
            stack.Children.Add(Card(new VerticalStackLayout
            {
                Spacing = 10,
                Children =
                {
                    Text("種目がまだありません", 18, bold: true),
                    Action("メニューから選ぶ", OnAddMenuClicked),
                    Action("＋ 種目を追加", OnAddExerciseClicked),
                    Text("途中からでもメニューや種目を追加できます。", 14, Secondary)
                }
            }));
        }
        foreach (var exercise in workout.Exercises)
            stack.Children.Add(BuildExerciseCard(exercise));
        stack.Children.Add(BuildRestTimerCard());
        stack.Children.Add(Action("ワークアウトを完了", OnFinishClicked, primary: true));
        stack.Children.Add(DangerAction("ワークアウトを中止", OnCancelClicked));
        return stack;
    }

    private View BuildRestTimerCard()
    {
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(Text("休憩タイマー", 18, bold: true));
        _restTimerState = Text(RestTimerText(), 18, Accent, true);
        stack.Children.Add(_restTimerState);
        var presets = new HorizontalStackLayout { Spacing = 8 };
        foreach (var seconds in new[] { 60, 90, 120 })
            presets.Children.Add(Action($"{seconds}秒", (_, _) => { _workouts.StartRestTimer(TimeSpan.FromSeconds(seconds)); UpdateStateLabels(); }));
        stack.Children.Add(presets);
        var controls = new HorizontalStackLayout { Spacing = 8 };
        controls.Children.Add(Action("+15秒", (_, _) => { _workouts.AddRestTimer(TimeSpan.FromSeconds(15)); UpdateStateLabels(); }));
        controls.Children.Add(Action("停止", (_, _) => { _workouts.StopRestTimer(); UpdateStateLabels(); }));
        stack.Children.Add(controls);
        stack.Children.Add(Text("この端末で画面表示中に使うタイマーです。", 12, Secondary));
        return Card(stack);
    }

    private View BuildExerciseCard(ActiveWorkoutExerciseDto exercise)
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Children.Add(Text(exercise.ExerciseName, 20, bold: true));
        var previous = exercise.PreviousPerformance?.Sets.FirstOrDefault();
        stack.Children.Add(Text(previous is null ? "前回の記録: なし" : $"前回の記録: {previous.WeightKg:0.##} kg × {previous.Reps}", 13, Secondary));
        foreach (var set in exercise.Sets)
            stack.Children.Add(BuildSetRow(set));
        stack.Children.Add(Action("セットを追加", (_, _) => { _workouts.AddSet(exercise.WorkoutExerciseId); Render(); }));
        return Card(stack);
    }

    private View BuildSetRow(ActiveWorkoutSetDto set)
    {
        var row = new Grid { ColumnSpacing = 8, RowSpacing = 3 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        row.Add(Text($"{set.SetNumber}", 16, Accent, true), 0, 0);
        row.Add(NumberEntry("kg", set.WeightKg?.ToString("0.##", CultureInfo.InvariantCulture), text => _workouts.UpdateWeight(set.WorkoutSetId, text)), 1, 0);
        row.Add(NumberEntry("回", set.Reps?.ToString(CultureInfo.InvariantCulture), text => _workouts.UpdateReps(set.WorkoutSetId, text)), 2, 0);
        row.Add(NumberEntry("RPE", set.Rpe?.ToString("0.#", CultureInfo.InvariantCulture), text => _workouts.UpdateRpe(set.WorkoutSetId, text)), 3, 0);
        return row;
    }

    private static Entry NumberEntry(string placeholder, string? value, Func<string, bool> update)
    {
        var entry = new Entry
        {
            Placeholder = placeholder,
            Text = value,
            Keyboard = Keyboard.Numeric,
            TextColor = Primary,
            PlaceholderColor = Secondary,
            BackgroundColor = Color.FromArgb("#303030"),
            MinimumHeightRequest = 48,
            HorizontalTextAlignment = TextAlignment.Center
        };
        entry.TextChanged += (_, args) => update(args.NewTextValue ?? string.Empty);
        return entry;
    }

    private async void OnStartEmptyClicked(object? sender, EventArgs e) => await StartMenuAsync(null);
    private async Task StartMenuAsync(Guid? menuId)
    {
        try { await _workouts.StartAsync(menuId); Render(); }
        catch { _error?.Text = "ワークアウトを開始できませんでした。"; }
    }

    private async void OnFinishClicked(object? sender, EventArgs e)
    {
        if (await _workouts.FinishAsync()) await Shell.Current.GoToAsync("//home");
        else UpdateStateLabels();
    }

    private async void OnAddExerciseClicked(object? sender, EventArgs e) => await AddExerciseAsync();

    private async Task AddExerciseAsync()
    {
        try
        {
            var exercises = await _workouts.GetAddableExercisesAsync();
            if (exercises.Count == 0)
            {
                var openTrainingData = await DisplayAlertAsync("使用できる種目がありません", "有効な種目をトレーニングデータで作成してから追加してください。", "トレーニングデータを開く", "キャンセル");
                if (openTrainingData) await Navigation.PushAsync(new TrainingDataPage(_workouts));
                return;
            }

            var labels = exercises.Select(x => x.Name).ToArray();
            var selected = await DisplayActionSheetAsync("追加する種目を選ぶ", "キャンセル", null, labels);
            var index = Array.IndexOf(labels, selected);
            if (index < 0) return;
            await _workouts.AddExerciseToActiveWorkoutAsync(exercises[index].Id);
            Render();
        }
        catch { _error?.Text = "種目を追加できませんでした。"; }
    }

    private async void OnAddMenuClicked(object? sender, EventArgs e) => await AddMenuAsync();

    private async Task AddMenuAsync()
    {
        try
        {
            var menus = await _workouts.GetMasterMenusAsync();
            if (menus.Count == 0)
            {
                var openTrainingData = await DisplayAlertAsync("利用可能なメニューはありません", "トレーニングデータでメニューを作成してから追加してください。", "トレーニングデータを開く", "キャンセル");
                if (openTrainingData) await Navigation.PushAsync(new TrainingDataPage(_workouts));
                return;
            }

            var labels = menus.Select(x => $"{x.Name}（{x.Exercises.Count}種目）").ToArray();
            var selected = await DisplayActionSheetAsync("メニューから種目を追加", "キャンセル", null, labels);
            var index = Array.IndexOf(labels, selected);
            if (index < 0) return;
            await _workouts.AddMenuToActiveWorkoutAsync(menus[index].Id);
            Render();
        }
        catch { _error?.Text = "メニューの種目を追加できませんでした。"; }
    }

    private async void OnCancelClicked(object? sender, EventArgs e)
    {
        var confirmed = await DisplayAlertAsync("ワークアウトを中止しますか？", "現在のワークアウトの下書きは削除されます。履歴には保存されません。", "中止する", "続ける");
        if (!confirmed) return;
        if (await _workouts.CancelActiveWorkoutAsync()) await Shell.Current.GoToAsync("//home");
        else UpdateStateLabels();
    }

    private void UpdateStateLabels()
    {
        if (_saveState is not null) _saveState.Text = _workouts.SaveStateText;
        if (_error is not null) _error.Text = _workouts.LastError ?? string.Empty;
        if (_restTimerState is not null) _restTimerState.Text = RestTimerText();
    }

    private string RestTimerText()
    {
        var remaining = _workouts.RestTimerRemaining;
        if (_workouts.IsRestTimerRunning) return $"残り {remaining.Minutes:00}:{remaining.Seconds:00}";
        return remaining == TimeSpan.Zero ? "タイマー停止中" : $"残り {remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    private void EnsureRestTimerTicking()
    {
        if (_timerTicking) return;
        _timerTicking = true;
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            UpdateStateLabels();
            if (IsVisible) return true;
            _timerTicking = false;
            return false;
        });
    }
}

internal sealed class HistoryPage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;
    private readonly List<WorkoutHistoryItem> _items = [];
    private HistoryFilterOptions? _filters;
    private HistoryCursor? _nextCursor;
    private bool _showFilters;
    private bool _loading;
    private DateOnly? _from;
    private DateOnly? _to;
    private Guid? _exerciseId;
    private Guid? _muscleId;
    private Guid? _menuId;
    private Switch? _useFrom;
    private Switch? _useTo;
    private DatePicker? _fromPicker;
    private DatePicker? _toPicker;
    private Picker? _exercisePicker;
    private Picker? _musclePicker;
    private Picker? _menuPicker;

    public HistoryPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "History";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { await LoadFirstAsync(); }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("履歴を読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 14 };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Add(Text("履歴", 28, bold: true), 0, 0);
        header.Add(Action(_showFilters ? "閉じる" : "絞り込み", (_, _) => { _showFilters = !_showFilters; Render(); }), 1, 0);
        stack.Children.Add(header);
        stack.Children.Add(Text("完了済みの記録をこの端末内から表示します。", 14, Secondary));

        if (_showFilters) stack.Children.Add(BuildFilterCard());
        if (_loading) stack.Children.Add(Card(Text("読み込み中...", 14, Secondary)));
        else if (_items.Count == 0) stack.Children.Add(Card(Text("まだワークアウト履歴がありません", 15, Secondary)));
        else foreach (var item in _items) stack.Children.Add(BuildHistoryCard(item));

        if (!_loading && _nextCursor is not null)
            stack.Children.Add(Action("さらに読み込む", async (_, _) => await LoadMoreAsync()));
        Content = new ScrollView { Content = stack };
    }

    private View BuildFilterCard()
    {
        var filters = _filters ?? new HistoryFilterOptions([], [], []);
        _useFrom = new Switch { IsToggled = _from.HasValue, OnColor = Accent };
        _fromPicker = new DatePicker { Date = (_from ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue), TextColor = Primary, BackgroundColor = Color.FromArgb("#303030") };
        _useTo = new Switch { IsToggled = _to.HasValue, OnColor = Accent };
        _toPicker = new DatePicker { Date = (_to ?? DateOnly.FromDateTime(DateTime.Today)).ToDateTime(TimeOnly.MinValue), TextColor = Primary, BackgroundColor = Color.FromArgb("#303030") };
        _exercisePicker = ChoicePicker("種目", filters.Exercises, _exerciseId);
        _musclePicker = ChoicePicker("部位", filters.PrimaryMuscles, _muscleId);
        _menuPicker = ChoicePicker("メニュー", filters.Menus, _menuId);

        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Children.Add(Text("絞り込み", 18, bold: true));
        stack.Children.Add(FilterDateRow("開始日", _useFrom, _fromPicker));
        stack.Children.Add(FilterDateRow("終了日", _useTo, _toPicker));
        stack.Children.Add(_exercisePicker);
        stack.Children.Add(_musclePicker);
        stack.Children.Add(_menuPicker);
        stack.Children.Add(Action("適用", async (_, _) => await ApplyFiltersAsync(), primary: true));
        stack.Children.Add(Action("条件をクリア", async (_, _) => await ClearFiltersAsync()));
        return Card(stack);
    }

    private static View FilterDateRow(string label, Switch enabled, DatePicker picker)
    {
        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        row.Add(Text(label, 14, Secondary), 0, 0);
        row.Add(enabled, 1, 0);
        row.Add(picker, 2, 0);
        return row;
    }

    private static Picker ChoicePicker(string title, IReadOnlyList<HistoryFilterOption> options, Guid? selectedId)
    {
        var choices = new[] { "すべて" }.Concat(options.Select(x => x.Name)).ToArray();
        var selectedIndex = selectedId is null ? 0 : Math.Max(0, options.ToList().FindIndex(x => x.Id == selectedId.Value) + 1);
        return new Picker
        {
            Title = title,
            ItemsSource = choices,
            SelectedIndex = selectedIndex,
            TextColor = Primary,
            TitleColor = Secondary,
            BackgroundColor = Color.FromArgb("#303030"),
            MinimumHeightRequest = 48
        };
    }

    private View BuildHistoryCard(WorkoutHistoryItem item)
    {
        var stack = new VerticalStackLayout
        {
            Spacing = 5,
            Children =
            {
                Text(item.WorkoutDate.ToString("yyyy年M月d日"), 13, Accent, true),
                Text(item.Name ?? item.SourceMenuName ?? "ワークアウト", 17, bold: true),
                Text($"メニュー: {item.SourceMenuName ?? "なし"}", 13, Secondary),
                Text($"{item.ExerciseCount} 種目 ・ {item.TotalSets} セット ・ {item.TotalVolume:0.##} kg", 13, Secondary),
                Action("詳細を開く", async (_, _) => await Navigation.PushAsync(new WorkoutDetailPage(_workouts, item.Id)))
            }
        };
        return Card(stack);
    }

    private async Task LoadFirstAsync()
    {
        if (_loading) return;
        _loading = true;
        Render();
        try
        {
            _filters ??= await _workouts.GetHistoryFilterOptionsAsync();
            var page = await _workouts.GetHistoryPageAsync(BuildQuery(null));
            _items.Clear();
            _items.AddRange(page.Items);
            _nextCursor = page.NextCursor;
        }
        finally { _loading = false; Render(); }
    }

    private async Task LoadMoreAsync()
    {
        if (_loading || _nextCursor is null) return;
        _loading = true;
        Render();
        try
        {
            var page = await _workouts.GetHistoryPageAsync(BuildQuery(_nextCursor));
            var existing = _items.Select(x => x.Id).ToHashSet();
            _items.AddRange(page.Items.Where(x => existing.Add(x.Id)));
            _nextCursor = page.NextCursor;
        }
        finally { _loading = false; Render(); }
    }

    private async Task ApplyFiltersAsync()
    {
        _from = _useFrom?.IsToggled == true ? ToDateOnly(_fromPicker) : null;
        _to = _useTo?.IsToggled == true ? ToDateOnly(_toPicker) : null;
        _exerciseId = SelectedOptionId(_exercisePicker, _filters?.Exercises ?? []);
        _muscleId = SelectedOptionId(_musclePicker, _filters?.PrimaryMuscles ?? []);
        _menuId = SelectedOptionId(_menuPicker, _filters?.Menus ?? []);
        _showFilters = false;
        await LoadFirstAsync();
    }

    private async Task ClearFiltersAsync()
    {
        _from = null; _to = null; _exerciseId = null; _muscleId = null; _menuId = null; _showFilters = false;
        await LoadFirstAsync();
    }

    private HistoryQuery BuildQuery(HistoryCursor? cursor) => new(
        _from,
        _to?.AddDays(1),
        _exerciseId,
        _muscleId,
        _menuId,
        25,
        cursor);

    private static Guid? SelectedOptionId(Picker? picker, IReadOnlyList<HistoryFilterOption> options) =>
        picker is null || picker.SelectedIndex <= 0 || picker.SelectedIndex > options.Count ? null : options[picker.SelectedIndex - 1].Id;

    private static DateOnly? ToDateOnly(DatePicker? picker) => picker?.Date is DateTime value ? DateOnly.FromDateTime(value) : null;
}

internal sealed class WorkoutDetailPage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;
    private readonly Guid _sessionId;

    public WorkoutDetailPage(MobileWorkoutController workouts, Guid sessionId)
    {
        _workouts = workouts;
        _sessionId = sessionId;
        Title = "記録の詳細";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try { Render(await _workouts.GetWorkoutDetailAsync(_sessionId)); }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("記録の詳細を読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }

    private void Render(WorkoutDetailDto? workout)
    {
        if (workout is null)
        {
            Content = new VerticalStackLayout { Padding = 20, Children = { Text("この完了記録は見つかりません。", 16, Secondary) } };
            return;
        }

        var stack = new VerticalStackLayout { Padding = 20, Spacing = 14 };
        stack.Children.Add(Text("記録の詳細", 28, bold: true));
        stack.Children.Add(Card(new VerticalStackLayout
        {
            Spacing = 5,
            Children =
            {
                Text(workout.Name ?? workout.SourceMenuName ?? "ワークアウト", 20, bold: true),
                Text($"{workout.WorkoutDate:yyyy年M月d日} ・ {workout.TotalSets} セット ・ {workout.TotalVolume:0.##} kg", 14, Secondary),
                Text($"メニュー: {workout.SourceMenuName ?? "なし"}", 14, Secondary),
                Text(string.IsNullOrWhiteSpace(workout.Note) ? "メモ: なし" : $"メモ: {workout.Note}", 14, Secondary)
            }
        }));
        foreach (var exercise in workout.Exercises.OrderBy(x => x.DisplayOrder)) stack.Children.Add(BuildExercise(exercise));
        Content = new ScrollView { Content = stack };
    }

    private static View BuildExercise(WorkoutDetailExerciseDto exercise)
    {
        var stack = new VerticalStackLayout { Spacing = 6 };
        stack.Children.Add(Text(exercise.ExerciseNameSnapshot, 18, bold: true));
        stack.Children.Add(Text(exercise.PrimaryMuscleNameSnapshot, 13, Accent));
        stack.Children.Add(Text(string.IsNullOrWhiteSpace(exercise.Note) ? "メモ: なし" : $"メモ: {exercise.Note}", 13, Secondary));
        foreach (var set in exercise.Sets.OrderBy(x => x.SetNumber))
        {
            var volume = set.WeightKg.HasValue && set.Reps.HasValue ? $" ・ {set.WeightKg.Value * set.Reps.Value:0.##} kg" : string.Empty;
            stack.Children.Add(Text($"{set.SetNumber}セット: {set.WeightKg?.ToString("0.##") ?? "-"} kg × {set.Reps?.ToString() ?? "-"} 回 ・ RPE {set.Rpe?.ToString("0.#") ?? "-"}{volume}", 14, Secondary));
        }
        stack.Children.Add(Text($"種目ボリューム: {exercise.Volume:0.##} kg", 13, Secondary));
        return Card(stack);
    }
}

internal sealed class MorePage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;

    public MorePage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "More";
        BackgroundColor = PageBackground;
        Content = new VerticalStackLayout
        {
            Padding = 20,
            Spacing = 14,
            Children =
            {
                Text("その他", 28, bold: true),
                Text("履歴の月表示はここから開けます。", 14, Secondary),
                Action("トレーニングデータ", async (_, _) => await Navigation.PushAsync(new TrainingDataPage(_workouts)), primary: true),
                Action("カレンダーを開く", async (_, _) => await Navigation.PushAsync(new CalendarPage(_workouts))),
                Action("データ", async (_, _) => await Navigation.PushAsync(new TransferPage(_workouts)))
            }
        };
    }
}

internal sealed class TransferPage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;
    private Label? _status;

    public TransferPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "データ";
        BackgroundColor = PageBackground;
        Render();
    }

    private void Render()
    {
        _status = Text("完了済みワークアウトとマスターデータを.wtbで手動転送します。進行中のDraftとRest Timerは含まれません。", 14, Secondary);
        Content = new VerticalStackLayout
        {
            Padding = 20,
            Spacing = 14,
            Children =
            {
                Text("データ", 28, bold: true),
                _status,
                Action("ファイルに保存", SaveToFileAsync, primary: true),
                Action("共有", ShareAsync),
                Action("データをインポート", ImportAsync),
            }
        };
    }

    private async void SaveToFileAsync(object? sender, EventArgs args)
    {
        try
        {
            var result = await _workouts.SaveTransferToFileAsync();
            _status!.Text = result.Saved ? $"ファイルに保存しました。\n{result.Destination}" : "ファイル保存をキャンセルしました。";
        }
        catch (Exception ex) { _status!.Text = $"ファイル保存に失敗しました: {ex.Message}"; }
    }

    private async void ShareAsync(object? sender, EventArgs args)
    {
        try { await _workouts.RequestTransferShareAsync(); _status!.Text = "共有画面を開きました。"; }
        catch (Exception ex) { _status!.Text = $"共有を開始できませんでした: {ex.Message}"; }
    }

    private async void ImportAsync(object? sender, EventArgs args)
    {
        try
        {
            var plan = await _workouts.PickImportPlanAsync();
            if (plan is null) return;
            var confirmed = await DisplayAlertAsync("インポート確認", $"転送ファイルを検証しました。\n\n追加: {plan.NewItems.Count}\n変更なし: {plan.UnchangedItems.Count}\n競合: {plan.Conflicts.Count}\n\n競合解決後にインポートを実行します。続行しますか？", "続行", "キャンセル");
            if (!confirmed) return;
            var resolutions = new List<ImportResolution>();
            foreach (var conflict in plan.Conflicts)
            {
                var useImport = await DisplayAlertAsync("競合を検出しました", $"{conflict.Label}\n\nインポート版を使用しますか？", "インポート版", "ローカル保持");
                resolutions.Add(new(conflict.Kind, conflict.Id, useImport));
            }
            var result = await _workouts.ImportTransferAsync(plan, resolutions);
            _status!.Text = $"インポート完了。追加 {result.Added} / 更新 {result.Updated} / 変更なし {result.Unchanged}";
        }
        catch (Exception ex) { _status!.Text = $"インポート失敗: {ex.Message}"; }
    }
}

internal sealed class CalendarPage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;
    private DateOnly _month = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private CalendarMonthDto? _calendar;
    private DateOnly? _selectedDate;
    private bool _loading;

    public CalendarPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "カレンダー";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadMonthAsync();
    }

    private async Task LoadMonthAsync()
    {
        if (_loading) return;
        _loading = true;
        Render();
        try { _calendar = await _workouts.GetCalendarMonthAsync(_month.Year, _month.Month); }
        catch { _calendar = null; }
        finally { _loading = false; Render(); }
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        stack.Children.Add(Text("カレンダー", 28, bold: true));
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.Add(Action("‹ 前月", async (_, _) => { _month = _month.AddMonths(-1); _selectedDate = null; await LoadMonthAsync(); }), 0, 0);
        var monthLabel = Text($"{_month:yyyy年M月}", 20, bold: true);
        monthLabel.HorizontalTextAlignment = TextAlignment.Center;
        monthLabel.VerticalTextAlignment = TextAlignment.Center;
        controls.Add(monthLabel, 1, 0);
        controls.Add(Action("次月 ›", async (_, _) => { _month = _month.AddMonths(1); _selectedDate = null; await LoadMonthAsync(); }), 2, 0);
        stack.Children.Add(controls);
        if (_loading) stack.Children.Add(Card(Text("読み込み中...", 14, Secondary)));
        else if (_calendar is null) stack.Children.Add(Card(Text("カレンダーを読み込めませんでした。", 14, Color.FromArgb("#EF9A9A"))));
        else
        {
            stack.Children.Add(BuildMonthGrid(_calendar));
            stack.Children.Add(BuildSelectedDay(_calendar));
        }
        Content = new ScrollView { Content = stack };
    }

    private View BuildMonthGrid(CalendarMonthDto calendar)
    {
        var grid = new Grid { ColumnSpacing = 3, RowSpacing = 3 };
        for (var column = 0; column < 7; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        for (var row = 0; row < 7; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var weekdays = new[] { "日", "月", "火", "水", "木", "金", "土" };
        for (var column = 0; column < 7; column++)
        {
            var weekday = Text(weekdays[column], 13, Secondary, true);
            weekday.HorizontalTextAlignment = TextAlignment.Center;
            grid.Add(weekday, column, 0);
        }
        var firstOffset = (int)new DateTime(calendar.Year, calendar.Month, 1).DayOfWeek;
        foreach (var day in calendar.Days)
        {
            var index = firstOffset + day.Date.Day - 1;
            var row = index / 7 + 1;
            var column = index % 7;
            var isSelected = _selectedDate == day.Date;
            var isToday = day.Date == DateOnly.FromDateTime(DateTime.Today);
            var button = new Button
            {
                Text = day.HasWorkouts ? $"{day.Date.Day}\n● {day.WorkoutCount}" : day.Date.Day.ToString(),
                MinimumHeightRequest = 48,
                FontSize = 13,
                Padding = new Thickness(1),
                BackgroundColor = isSelected ? Accent : isToday ? Color.FromArgb("#34514E") : Surface,
                TextColor = isSelected ? Color.FromArgb("#10201E") : Primary
            };
            button.Clicked += (_, _) => { _selectedDate = day.Date; Render(); };
            grid.Add(button, column, row);
        }
        return Card(grid);
    }

    private View BuildSelectedDay(CalendarMonthDto calendar)
    {
        var day = _selectedDate is null ? null : calendar.Days.SingleOrDefault(x => x.Date == _selectedDate.Value);
        if (day is null) return Card(Text("日付を選択すると、その日のワークアウトを表示します。", 14, Secondary));
        var stack = new VerticalStackLayout { Spacing = 8 };
        stack.Children.Add(Text($"{day.Date:yyyy年M月d日}", 18, bold: true));
        if (!day.HasWorkouts) stack.Children.Add(Text("この日のワークアウトはありません。", 14, Secondary));
        foreach (var workout in day.Workouts)
        {
            var label = $"{workout.Name ?? workout.SourceMenuName ?? "ワークアウト"} ・ {workout.TotalSets} セット ・ {workout.TotalVolume:0.##} kg";
            stack.Children.Add(Action(label, async (_, _) => await Navigation.PushAsync(new WorkoutDetailPage(_workouts, workout.WorkoutSessionId))));
        }
        return Card(stack);
    }
}

internal sealed class PlaceholderPage : MobilePageBase
{
    public PlaceholderPage(string title)
    {
        Title = title;
        BackgroundColor = PageBackground;
        Content = new VerticalStackLayout
        {
            Padding = 20,
            Spacing = 10,
            Children = { Text(title, 28, bold: true), Text("この画面はM1の対象外です。", 14, Secondary) }
        };
    }
}
