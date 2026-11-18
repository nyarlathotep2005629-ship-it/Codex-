using WorkoutTracker.Application;

namespace WorkoutTracker.Mobile;

internal sealed class WorkoutExercisePickerPage : MobilePageBase
{
    private readonly IReadOnlyList<ExerciseMasterDto> _exercises;
    private readonly Func<Guid, Task> _selected;
    private readonly VerticalStackLayout _results = new() { Spacing = 10 };
    private readonly Label _status = Text(string.Empty, 14, Color.FromArgb("#EF9A9A"));
    private string _search = string.Empty;
    private string _muscle = "すべて";
    private bool _selecting;

    public WorkoutExercisePickerPage(IReadOnlyList<ExerciseMasterDto> exercises, Func<Guid, Task> selected)
    {
        _exercises = exercises;
        _selected = selected;
        Title = "種目を選ぶ";
        BackgroundColor = PageBackground;
        var stack = new VerticalStackLayout { Padding = 16, Spacing = 12 };
        stack.Children.Add(Text("種目を追加", 28, bold: true));
        stack.Children.Add(Text("名前または主部位で絞り込んでください。", 14, Secondary));
        var search = new Entry { Placeholder = "種目名で検索", TextColor = Primary, PlaceholderColor = Secondary, BackgroundColor = Color.FromArgb("#26363D"), MinimumHeightRequest = 48 };
        search.TextChanged += (_, _) => { _search = search.Text?.Trim() ?? string.Empty; RenderResults(); };
        stack.Children.Add(search);
        var muscles = new[] { "すべて" }.Concat(exercises.Select(x => x.PrimaryMuscleName).Distinct().OrderBy(x => x)).ToArray();
        var filter = new Picker { Title = "主部位", ItemsSource = muscles, SelectedIndex = 0, TextColor = Primary, BackgroundColor = Color.FromArgb("#26363D"), MinimumHeightRequest = 48 };
        filter.SelectedIndexChanged += (_, _) => { _muscle = filter.SelectedIndex < 0 ? "すべて" : muscles[filter.SelectedIndex]; RenderResults(); };
        stack.Children.Add(filter);
        stack.Children.Add(_status);
        stack.Children.Add(_results);
        Content = new ScrollView { Content = stack };
        RenderResults();
    }

    private void RenderResults()
    {
        _results.Children.Clear();
        var matches = _exercises.Where(x => (_muscle == "すべて" || x.PrimaryMuscleName == _muscle) &&
            (_search.Length == 0 || x.Name.Contains(_search, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        _results.Children.Add(Text($"{matches.Length} 件", 14, Secondary));
        if (matches.Length == 0) _results.Children.Add(Card(Text("一致する種目がありません。", 14, Secondary)));
        if (matches.Length > 40) _results.Children.Add(Text("先頭40件を表示中です。検索または主部位で絞り込んでください。", 13, Secondary));
        foreach (var exercise in matches.Take(40))
        {
            var row = new VerticalStackLayout { Spacing = 5 };
            row.Children.Add(Text(exercise.Name, 17, bold: true));
            row.Children.Add(Text(exercise.PrimaryMuscleName, 13, Secondary));
            row.Children.Add(Action("この種目を追加", async (_, _) => await SelectAsync(exercise.Id), primary: true));
            _results.Children.Add(Card(row));
        }
    }

    private async Task SelectAsync(Guid id)
    {
        if (_selecting) return;
        _selecting = true;
        try { await _selected(id); await Navigation.PopAsync(); }
        catch { _status.Text = "種目を追加できませんでした。再試行してください。"; }
        finally { _selecting = false; }
    }
}
