using System.Globalization;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Mobile;

/// <summary>Mobile-only presentation over the shared MasterDataService contract.</summary>
internal abstract class TrainingDataPageBase : MobilePageBase
{
    protected static Entry Field(string placeholder, string? value = null, bool numeric = false) => new()
    {
        Placeholder = placeholder,
        Text = value,
        Keyboard = numeric ? Keyboard.Numeric : Keyboard.Text,
        TextColor = Primary,
        PlaceholderColor = Secondary,
        BackgroundColor = Color.FromArgb("#303030"),
        MinimumHeightRequest = 48
    };

    protected static Label ErrorLabel() => Text(string.Empty, 14, Color.FromArgb("#EF9A9A"));
    protected static string FriendlyUnexpectedError => "操作を完了できませんでした。もう一度お試しください。";
}

internal sealed class TrainingDataPage : TrainingDataPageBase
{
    private readonly MobileWorkoutController _workouts;
    private bool _loading;

    public TrainingDataPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "トレーニングデータ";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loading) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        Content = new VerticalStackLayout { Padding = 20, Children = { Text("トレーニングデータ", 28, bold: true), Text("読み込み中...", 14, Secondary) } };
        try
        {
            var musclesTask = _workouts.GetMuscleGroupsAsync();
            var exercisesTask = _workouts.GetExercisesAsync();
            var menusTask = _workouts.GetMasterMenusAsync();
            await Task.WhenAll(musclesTask, exercisesTask, menusTask);
            var stack = new VerticalStackLayout { Padding = 20, Spacing = 14 };
            stack.Children.Add(Text("トレーニングデータ", 28, bold: true));
            stack.Children.Add(Text("この端末内で、部位・種目・メニューを作成できます。", 14, Secondary));
            stack.Children.Add(NavigationCard("メニュー", $"{(await menusTask).Count} 件", async () => await Navigation.PushAsync(new MenuListPage(_workouts))));
            stack.Children.Add(NavigationCard("種目", $"{(await exercisesTask).Count} 件", async () => await Navigation.PushAsync(new ExerciseListPage(_workouts))));
            stack.Children.Add(NavigationCard("部位", $"{(await musclesTask).Count} 件", async () => await Navigation.PushAsync(new MuscleGroupListPage(_workouts))));
            Content = new ScrollView { Content = stack };
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("トレーニングデータを読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
        finally { _loading = false; }
    }

    private static View NavigationCard(string title, string summary, Func<Task> navigate) => Card(new VerticalStackLayout
    {
        Spacing = 6,
        Children = { Text(title, 19, bold: true), Text(summary, 14, Secondary), Action("開く", async (_, _) => await navigate()) }
    });
}

internal sealed class MuscleGroupListPage : TrainingDataPageBase
{
    private readonly MobileWorkoutController _workouts;

    public MuscleGroupListPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "部位";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var muscles = await _workouts.GetMuscleGroupsAsync();
            var stack = new VerticalStackLayout { Padding = 20, Spacing = 12 };
            stack.Children.Add(Text("部位", 28, bold: true));
            stack.Children.Add(Action("部位を追加", async (_, _) => await Navigation.PushAsync(new MuscleGroupEditorPage(_workouts, null)), primary: true));
            if (muscles.Count == 0) stack.Children.Add(Card(Text("部位がありません。まず部位を追加してください。", 14, Secondary)));
            foreach (var muscle in muscles)
            {
                stack.Children.Add(Card(new VerticalStackLayout
                {
                    Spacing = 5,
                    Children = { Text(muscle.Name, 18, bold: true), Text($"表示順: {muscle.DisplayOrder + 1}", 13, Secondary), Action("編集", async (_, _) => await Navigation.PushAsync(new MuscleGroupEditorPage(_workouts, muscle))) }
                }));
            }
            Content = new ScrollView { Content = stack };
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("部位を読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }
}

internal sealed class MuscleGroupEditorPage : TrainingDataPageBase
{
    private readonly MobileWorkoutController _workouts;
    private readonly MuscleGroupDto? _existing;
    private Entry? _name;
    private Entry? _order;
    private Label? _error;

    public MuscleGroupEditorPage(MobileWorkoutController workouts, MuscleGroupDto? existing)
    {
        _workouts = workouts;
        _existing = existing;
        Title = existing is null ? "部位を追加" : "部位を編集";
        BackgroundColor = PageBackground;
        Render();
    }

    private void Render()
    {
        _name = Field("部位名", _existing?.Name);
        _order = Field("表示順（0以上）", _existing?.DisplayOrder.ToString(CultureInfo.InvariantCulture) ?? "0", true);
        _error = ErrorLabel();
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        stack.Children.Add(Text(Title, 28, bold: true));
        stack.Children.Add(Text("部位名", 14, Secondary)); stack.Children.Add(_name);
        stack.Children.Add(Text("表示順", 14, Secondary)); stack.Children.Add(_order);
        stack.Children.Add(_error);
        stack.Children.Add(Action("保存", async (_, _) => await SaveAsync(), primary: true));
        if (_existing is not null) stack.Children.Add(Action("削除", async (_, _) => await DeleteAsync()));
        Content = new ScrollView { Content = stack };
    }

    private async Task SaveAsync()
    {
        var name = _name?.Text?.Trim() ?? string.Empty;
        if (name.Length == 0) { _error!.Text = "部位名を入力してください。"; return; }
        if (!int.TryParse(_order?.Text, out var order) || order < 0) { _error!.Text = "表示順は0以上の整数で入力してください。"; return; }
        try { await _workouts.SaveMuscleGroupAsync(new(_existing?.Id, name, order)); await Navigation.PopAsync(); }
        catch (DomainValidationException) { _error!.Text = "入力内容を確認してください。同じ名前は使用できません。"; }
        catch { _error!.Text = FriendlyUnexpectedError; }
    }

    private async Task DeleteAsync()
    {
        try
        {
            var result = await _workouts.DeleteMuscleGroupAsync(_existing!.Id);
            if (result == MuscleGroupDeleteResult.Deleted) { await Navigation.PopAsync(); return; }
            _error!.Text = result == MuscleGroupDeleteResult.Referenced ? "この部位は種目で使用されているため削除できません。" : "部位が見つかりません。";
        }
        catch { _error!.Text = FriendlyUnexpectedError; }
    }
}

internal sealed class ExerciseListPage : TrainingDataPageBase
{
    private readonly MobileWorkoutController _workouts;

    public ExerciseListPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "種目";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var exercises = await _workouts.GetExercisesAsync();
            var stack = new VerticalStackLayout { Padding = 20, Spacing = 12 };
            stack.Children.Add(Text("種目", 28, bold: true));
            stack.Children.Add(Action("最初の種目を作成", async (_, _) => await Navigation.PushAsync(new ExerciseEditorPage(_workouts, null)), primary: true));
            if (exercises.Count == 0) stack.Children.Add(Card(Text("種目がありません。部位を作成してから種目を追加してください。", 14, Secondary)));
            foreach (var exercise in exercises)
            {
                var state = exercise.IsActive ? "使用中" : "非表示";
                stack.Children.Add(Card(new VerticalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        Text(exercise.Name, 18, bold: true),
                        Text($"メイン部位: {exercise.PrimaryMuscleName} ・ {state}", 13, Secondary),
                        Text(exercise.SecondaryMuscleNames.Count == 0 ? "補助部位: なし" : $"補助部位: {string.Join("、", exercise.SecondaryMuscleNames)}", 13, Secondary),
                        Action("編集", async (_, _) => await Navigation.PushAsync(new ExerciseEditorPage(_workouts, exercise)))
                    }
                }));
            }
            Content = new ScrollView { Content = stack };
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("種目を読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }
}

internal sealed class ExerciseEditorPage : TrainingDataPageBase
{
    private readonly MobileWorkoutController _workouts;
    private readonly ExerciseMasterDto? _existing;
    private IReadOnlyList<MuscleGroupDto> _muscles = [];
    private readonly Dictionary<Guid, Switch> _secondary = [];
    private Entry? _name;
    private Editor? _note;
    private Picker? _primary;
    private Switch? _active;
    private Label? _error;

    public ExerciseEditorPage(MobileWorkoutController workouts, ExerciseMasterDto? existing)
    {
        _workouts = workouts;
        _existing = existing;
        Title = existing is null ? "種目を追加" : "種目を編集";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try { _muscles = await _workouts.GetMuscleGroupsAsync(); Render(); }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("種目の編集画面を開けませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }

    private void Render()
    {
        _name = Field("種目名", _existing?.Name);
        _note = new Editor { Placeholder = "メモ（任意）", Text = _existing?.Note, TextColor = Primary, PlaceholderColor = Secondary, BackgroundColor = Color.FromArgb("#303030"), MinimumHeightRequest = 96 };
        _active = new Switch { IsToggled = _existing?.IsActive ?? true, OnColor = Accent };
        _primary = new Picker { Title = "メイン部位を選択", ItemsSource = _muscles.Select(x => x.Name).ToArray(), TextColor = Primary, TitleColor = Secondary, BackgroundColor = Color.FromArgb("#303030"), MinimumHeightRequest = 48 };
        _primary.SelectedIndex = _existing is null ? (_muscles.Count > 0 ? 0 : -1) : _muscles.ToList().FindIndex(x => x.Id == _existing.PrimaryMuscleId);
        _error = ErrorLabel();
        _secondary.Clear();
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        stack.Children.Add(Text(Title, 28, bold: true));
        if (_muscles.Count == 0) stack.Children.Add(Card(Text("種目を作成するには、先に部位を追加してください。", 14, Secondary)));
        stack.Children.Add(Text("種目名", 14, Secondary)); stack.Children.Add(_name);
        stack.Children.Add(Text("メイン部位", 14, Secondary)); stack.Children.Add(_primary);
        stack.Children.Add(Text("補助部位", 14, Secondary));
        foreach (var muscle in _muscles)
        {
            var toggle = new Switch { IsToggled = _existing?.SecondaryMuscleIds.Contains(muscle.Id) == true, OnColor = Accent };
            _secondary[muscle.Id] = toggle;
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Add(Text(muscle.Name, 15), 0, 0); row.Add(toggle, 1, 0); stack.Children.Add(row);
        }
        var activeRow = new Grid { ColumnSpacing = 10 };
        activeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star }); activeRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        activeRow.Add(Text("使用中", 15), 0, 0); activeRow.Add(_active, 1, 0); stack.Children.Add(activeRow);
        stack.Children.Add(Text("メモ", 14, Secondary)); stack.Children.Add(_note); stack.Children.Add(_error);
        stack.Children.Add(Action("保存", async (_, _) => await SaveAsync(), primary: true));
        if (_existing is not null)
        {
            stack.Children.Add(Action(_existing.IsActive ? "非表示にする" : "使用中に戻す", async (_, _) => await SetActiveAsync(!_existing.IsActive)));
            stack.Children.Add(Action("削除", async (_, _) => await DeleteAsync()));
        }
        Content = new ScrollView { Content = stack };
    }

    private async Task SaveAsync()
    {
        var name = _name?.Text?.Trim() ?? string.Empty;
        if (name.Length == 0) { _error!.Text = "種目名を入力してください。"; return; }
        if (_primary?.SelectedIndex is not int index || index < 0 || index >= _muscles.Count) { _error!.Text = "メイン部位を選択してください。"; return; }
        var primary = _muscles[index];
        var secondary = _secondary.Where(x => x.Value.IsToggled).Select(x => x.Key).ToArray();
        if (secondary.Contains(primary.Id)) { _error!.Text = "メイン部位と補助部位には同じ部位を選択できません。"; return; }
        try { await _workouts.SaveExerciseAsync(new(_existing?.Id, name, primary.Id, secondary, _note?.Text, _active?.IsToggled ?? true)); await Navigation.PopAsync(); }
        catch (DomainValidationException) { _error!.Text = "入力内容を確認してください。同じ名前は使用できません。"; }
        catch { _error!.Text = FriendlyUnexpectedError; }
    }

    private async Task SetActiveAsync(bool active)
    {
        try { await _workouts.SetExerciseActiveAsync(_existing!.Id, active); await Navigation.PopAsync(); }
        catch { _error!.Text = FriendlyUnexpectedError; }
    }

    private async Task DeleteAsync()
    {
        try
        {
            var result = await _workouts.DeleteExerciseAsync(_existing!.Id);
            if (result == ExerciseDeleteResult.Deleted) { await Navigation.PopAsync(); return; }
            _error!.Text = result == ExerciseDeleteResult.ReferencedMustHide ? "この種目はメニューまたはワークアウトで使用されているため削除できません。非表示にできます。" : "種目が見つかりません。";
        }
        catch { _error!.Text = FriendlyUnexpectedError; }
    }
}

internal sealed class MenuListPage : TrainingDataPageBase
{
    private readonly MobileWorkoutController _workouts;

    public MenuListPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "メニュー";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var menus = await _workouts.GetMasterMenusAsync();
            var stack = new VerticalStackLayout { Padding = 20, Spacing = 12 };
            stack.Children.Add(Text("メニュー", 28, bold: true));
            stack.Children.Add(Action("最初のメニューを作成", async (_, _) => await Navigation.PushAsync(new MenuEditorPage(_workouts, null)), primary: true));
            if (menus.Count == 0) stack.Children.Add(Card(Text("メニューがありません。種目を追加してからメニューを作成してください。", 14, Secondary)));
            foreach (var menu in menus)
            {
                stack.Children.Add(Card(new VerticalStackLayout
                {
                    Spacing = 5,
                    Children =
                    {
                        Text(menu.Name, 18, bold: true),
                        Text($"{menu.Exercises.Count} 種目 ・ 予定 {menu.PlannedSetCount} セット", 13, Secondary),
                        Action("編集", async (_, _) => await Navigation.PushAsync(new MenuEditorPage(_workouts, menu)))
                    }
                }));
            }
            Content = new ScrollView { Content = stack };
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("メニューを読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }
}

internal sealed class MenuEditorPage : TrainingDataPageBase
{
    private sealed class DraftRow(Guid? id, ExerciseMasterDto exercise, int plannedSetCount, string? note)
    {
        public Guid? Id { get; } = id;
        public ExerciseMasterDto Exercise { get; } = exercise;
        public int PlannedSetCount { get; set; } = plannedSetCount;
        public string? Note { get; set; } = note;
    }

    private readonly MobileWorkoutController _workouts;
    private readonly WorkoutMenuMasterDto? _existing;
    private IReadOnlyList<ExerciseMasterDto> _exercises = [];
    private readonly List<DraftRow> _rows = [];
    private Entry? _name;
    private Editor? _description;
    private Picker? _exercisePicker;
    private Label? _error;
    private string? _draftName;
    private string? _draftDescription;

    public MenuEditorPage(MobileWorkoutController workouts, WorkoutMenuMasterDto? existing)
    {
        _workouts = workouts;
        _existing = existing;
        Title = existing is null ? "メニューを追加" : "メニューを編集";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _exercises = await _workouts.GetExercisesAsync();
            if (_rows.Count == 0 && _existing is not null)
            {
                foreach (var item in _existing.Exercises)
                {
                    var exercise = _exercises.SingleOrDefault(x => x.Id == item.ExerciseId);
                    if (exercise is not null) _rows.Add(new DraftRow(item.Id, exercise, item.DefaultSetCount, item.Note));
                }
            }
            Render();
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("メニューの編集画面を開けませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
    }

    private void Render()
    {
        // Adding, reordering, or removing a row redraws the form; keep unsaved header input intact.
        _draftName = _name?.Text ?? _draftName ?? _existing?.Name;
        _draftDescription = _description?.Text ?? _draftDescription ?? _existing?.Description;
        _name = Field("メニュー名", _draftName);
        _description = new Editor { Placeholder = "説明（任意）", Text = _draftDescription, TextColor = Primary, PlaceholderColor = Secondary, BackgroundColor = Color.FromArgb("#303030"), MinimumHeightRequest = 88 };
        var candidates = _exercises.Where(x => x.IsActive && _rows.All(row => row.Exercise.Id != x.Id)).ToArray();
        _exercisePicker = new Picker { Title = "追加する種目を選択", ItemsSource = candidates.Select(x => $"{x.Name}（{x.PrimaryMuscleName}）").ToArray(), TextColor = Primary, TitleColor = Secondary, BackgroundColor = Color.FromArgb("#303030"), MinimumHeightRequest = 48 };
        _error = ErrorLabel();
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 12 };
        stack.Children.Add(Text(Title, 28, bold: true));
        stack.Children.Add(Text("メニュー名", 14, Secondary)); stack.Children.Add(_name);
        stack.Children.Add(Text("説明", 14, Secondary)); stack.Children.Add(_description);
        stack.Children.Add(Text("種目を追加", 18, bold: true));
        if (_exercises.Count == 0) stack.Children.Add(Card(Text("種目がありません。先に種目を作成してください。", 14, Secondary)));
        else if (candidates.Length == 0) stack.Children.Add(Text("追加できる使用中の種目はありません。", 14, Secondary));
        else { stack.Children.Add(_exercisePicker); stack.Children.Add(Action("種目を追加", (_, _) => AddExercise(candidates))); }
        stack.Children.Add(Text("メニュー内の種目", 18, bold: true));
        if (_rows.Count == 0) stack.Children.Add(Card(Text("種目を追加してください。", 14, Secondary)));
        for (var index = 0; index < _rows.Count; index++) stack.Children.Add(BuildRow(_rows[index], index));
        stack.Children.Add(_error);
        stack.Children.Add(Action("保存", async (_, _) => await SaveAsync(), primary: true));
        if (_existing is not null) stack.Children.Add(Action("メニューを削除", async (_, _) => await DeleteAsync()));
        Content = new ScrollView { Content = stack };
    }

    private View BuildRow(DraftRow row, int index)
    {
        var count = Field("予定セット数", row.PlannedSetCount.ToString(CultureInfo.InvariantCulture), true);
        count.TextChanged += (_, args) => { if (int.TryParse(args.NewTextValue, out var value)) row.PlannedSetCount = value; else row.PlannedSetCount = 0; };
        var note = Field("種目メモ（任意）", row.Note);
        note.TextChanged += (_, args) => row.Note = args.NewTextValue;
        var controls = new Grid { ColumnSpacing = 6 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star }); controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Star });
        controls.Add(Action("上へ", (_, _) => Move(index, -1)), 0, 0); controls.Add(Action("下へ", (_, _) => Move(index, 1)), 1, 0);
        var stack = new VerticalStackLayout { Spacing = 7 };
        stack.Children.Add(Text($"{index + 1}. {row.Exercise.Name}", 17, bold: true));
        stack.Children.Add(Text($"メイン部位: {row.Exercise.PrimaryMuscleName}", 13, Secondary));
        stack.Children.Add(count); stack.Children.Add(note); stack.Children.Add(controls);
        stack.Children.Add(Action("削除", (_, _) => { _rows.Remove(row); Render(); }));
        return Card(stack);
    }

    private void AddExercise(IReadOnlyList<ExerciseMasterDto> candidates)
    {
        if (_exercisePicker?.SelectedIndex is not int index || index < 0 || index >= candidates.Count) { _error!.Text = "追加する種目を選択してください。"; return; }
        var exercise = candidates[index];
        if (_rows.Any(x => x.Exercise.Id == exercise.Id)) { _error!.Text = "同じ種目を複数追加できません。"; return; }
        _rows.Add(new DraftRow(null, exercise, 3, null));
        Render();
    }

    private void Move(int index, int delta)
    {
        var destination = index + delta;
        if (destination < 0 || destination >= _rows.Count) return;
        var row = _rows[index]; _rows.RemoveAt(index); _rows.Insert(destination, row); Render();
    }

    private async Task SaveAsync()
    {
        var name = _name?.Text?.Trim() ?? string.Empty;
        if (name.Length == 0) { _error!.Text = "メニュー名を入力してください。"; return; }
        if (_rows.Any(x => x.PlannedSetCount is < 1 or > 20)) { _error!.Text = "予定セット数は1〜20で入力してください。"; return; }
        try
        {
            var request = new SaveWorkoutMenuRequest(_existing?.Id, name, _description?.Text,
                _rows.Select(x => new SaveMenuExerciseRequest(x.Id, x.Exercise.Id, x.PlannedSetCount, x.Note)).ToArray());
            await _workouts.SaveMenuAsync(request);
            await Navigation.PopAsync();
        }
        catch (DomainValidationException) { _error!.Text = "入力内容を確認してください。同じ種目を複数追加することはできません。"; }
        catch { _error!.Text = FriendlyUnexpectedError; }
    }

    private async Task DeleteAsync()
    {
        try { await _workouts.DeleteMenuAsync(_existing!.Id); await Navigation.PopAsync(); }
        catch { _error!.Text = FriendlyUnexpectedError; }
    }
}
