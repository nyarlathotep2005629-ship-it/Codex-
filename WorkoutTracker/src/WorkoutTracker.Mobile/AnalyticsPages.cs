using Microsoft.Maui.Graphics;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.Mobile;

/// <summary>Statistics presentation only; calculations and SQLite access stay in AnalyticsService.</summary>
internal sealed class StatisticsPage : MobilePageBase
{
    private readonly MobileWorkoutController _workouts;
    private IReadOnlyList<ExerciseOptionDto> _exercises = [];
    private Picker? _exercisePicker;
    private Picker? _periodPicker;
    private Picker? _metricPicker;
    private StatisticsDto? _statistics;
    private bool _loading;

    public StatisticsPage(MobileWorkoutController workouts)
    {
        _workouts = workouts;
        Title = "Statistics";
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_exercises.Count == 0 && !_loading) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        Content = new VerticalStackLayout { Padding = 20, Children = { Text("統計", 28, bold: true), Text("読み込み中...", 14, Secondary) } };
        try
        {
            _exercises = await _workouts.GetAnalyticsExerciseOptionsAsync();
            Render();
            if (_exercises.Count > 0) await RefreshStatisticsAsync();
        }
        catch { Content = new VerticalStackLayout { Padding = 20, Children = { Text("統計を読み込めませんでした。", 16, Color.FromArgb("#EF9A9A")) } }; }
        finally { _loading = false; }
    }

    private void Render()
    {
        var stack = new VerticalStackLayout { Padding = 20, Spacing = 14 };
        stack.Children.Add(Text("統計", 28, bold: true));
        stack.Children.Add(Text("完了済みワークアウトの記録を、この端末内で集計します。", 14, Secondary));
        if (_exercises.Count == 0)
        {
            stack.Children.Add(Card(Text("統計を表示する種目がありません。トレーニングデータを作成し、ワークアウトを完了するとここに表示されます。", 14, Secondary)));
            Content = new ScrollView { Content = stack };
            return;
        }

        _exercisePicker = ChoicePicker("種目", _exercises.Select(x => x.Name).ToArray(), _exercisePicker?.SelectedIndex ?? 0);
        _periodPicker = ChoicePicker("期間", Enum.GetValues<DashboardPeriod>().Select(PeriodText).ToArray(), _periodPicker?.SelectedIndex ?? 1);
        _metricPicker = ChoicePicker("指標", Enum.GetValues<StatisticsMetric>().Select(MetricText).ToArray(), _metricPicker?.SelectedIndex ?? 0);
        stack.Children.Add(_exercisePicker);
        stack.Children.Add(_periodPicker);
        stack.Children.Add(_metricPicker);
        stack.Children.Add(Action("表示を更新", async (_, _) => await RefreshStatisticsAsync(), primary: true));
        if (_loading) stack.Children.Add(Card(Text("読み込み中...", 14, Secondary)));
        else if (_statistics is not null) stack.Children.Add(BuildStatisticsCard(_statistics));
        Content = new ScrollView { Content = stack };
    }

    private async Task RefreshStatisticsAsync()
    {
        if (_loading || _exercisePicker is null || _periodPicker is null || _metricPicker is null) return;
        var exerciseIndex = Math.Clamp(_exercisePicker.SelectedIndex, 0, _exercises.Count - 1);
        var periods = Enum.GetValues<DashboardPeriod>();
        var metrics = Enum.GetValues<StatisticsMetric>();
        var period = periods[Math.Clamp(_periodPicker.SelectedIndex, 0, periods.Length - 1)];
        var metric = metrics[Math.Clamp(_metricPicker.SelectedIndex, 0, metrics.Length - 1)];
        _loading = true;
        Render();
        try { _statistics = await _workouts.GetStatisticsAsync(_exercises[exerciseIndex].Id, period, metric, DateOnly.FromDateTime(DateTime.Today)); }
        catch { _statistics = null; }
        finally { _loading = false; Render(); }
    }

    private static Picker ChoicePicker(string title, IReadOnlyList<string> items, int index) => new()
    {
        Title = title,
        ItemsSource = items.ToList(),
        SelectedIndex = Math.Clamp(index, 0, items.Count - 1),
        TextColor = Primary,
        TitleColor = Secondary,
        BackgroundColor = Color.FromArgb("#303030"),
        MinimumHeightRequest = 48
    };

    private static View BuildStatisticsCard(StatisticsDto data)
    {
        var stack = new VerticalStackLayout { Spacing = 10 };
        stack.Children.Add(Text(data.ExerciseName, 20, bold: true));
        stack.Children.Add(Text($"{data.Range.Start:yyyy/M/d} 〜 {data.Range.EndExclusive.AddDays(-1):yyyy/M/d} ・ {data.WorkoutCount} 回", 13, Secondary));
        if (data.Series.Points.Count == 0)
        {
            stack.Children.Add(Text("この期間には完了済みの記録がありません。", 14, Secondary));
            return Card(stack);
        }

        var latest = data.Series.Points[^1];
        stack.Children.Add(Text($"{MetricText(data.Metric)}: {latest.Value:0.##} {data.Series.Unit}", 19, Accent, true));
        if (data.CurrentOneRepMax is not null || data.BestOneRepMax is not null || data.MaxWeight is not null)
        {
            var kpis = new List<string>();
            if (data.CurrentOneRepMax is not null) kpis.Add($"推定1RM {data.CurrentOneRepMax:0.##} kg");
            if (data.BestOneRepMax is not null) kpis.Add($"最高1RM {data.BestOneRepMax:0.##} kg");
            if (data.MaxWeight is not null) kpis.Add($"最大重量 {data.MaxWeight:0.##} kg");
            stack.Children.Add(Text(string.Join(" ・ ", kpis), 13, Secondary));
        }
        stack.Children.Add(new GraphicsView { HeightRequest = 210, Drawable = new StatisticsChartDrawable(data.Series) });
        return Card(stack);
    }

    private static string PeriodText(DashboardPeriod period) => period switch
    {
        DashboardPeriod.OneMonth => "1か月",
        DashboardPeriod.ThreeMonths => "3か月",
        DashboardPeriod.SixMonths => "6か月",
        DashboardPeriod.OneYear => "1年",
        _ => "すべて"
    };

    private static string MetricText(StatisticsMetric metric) => metric switch
    {
        StatisticsMetric.EstimatedOneRepMax => "推定1RM",
        StatisticsMetric.MaxWeight => "最大重量",
        StatisticsMetric.Volume => "ボリューム",
        StatisticsMetric.Sets => "セット数",
        _ => "レップ数"
    };
}

internal sealed class StatisticsChartDrawable(ChartSeriesDto series) : IDrawable
{
    private readonly ChartSeriesDto _series = series;

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        const float left = 42f;
        const float right = 10f;
        const float top = 18f;
        const float bottom = 28f;
        var points = _series.Points;
        if (points.Count == 0) return;
        var plot = new RectF(left, top, Math.Max(1, dirtyRect.Width - left - right), Math.Max(1, dirtyRect.Height - top - bottom));
        var min = points.Min(x => x.Value);
        var max = points.Max(x => x.Value);
        if (max == min) { min = Math.Max(0, min - 1); max += 1; }
        canvas.StrokeColor = Color.FromArgb("#505050");
        canvas.StrokeSize = 1;
        for (var i = 0; i < 4; i++)
        {
            var y = plot.Top + plot.Height * i / 3f;
            canvas.DrawLine(plot.Left, y, plot.Right, y);
            var value = max - (max - min) * i / 3m;
            canvas.FontColor = Color.FromArgb("#BDBDBD");
            canvas.FontSize = 10;
            canvas.DrawString(value.ToString("0.#"), new RectF(0, y - 8, left - 5, 16), HorizontalAlignment.Right, VerticalAlignment.Center);
        }

        PointF ToPoint(int index, ChartPointDto point) => new(
            plot.Left + (points.Count == 1 ? plot.Width / 2 : plot.Width * index / (points.Count - 1)),
            plot.Bottom - (float)((point.Value - min) / (max - min)) * plot.Height);
        canvas.StrokeColor = Color.FromArgb("#80CBC4");
        canvas.StrokeSize = 3;
        for (var i = 1; i < points.Count; i++)
        {
            var previous = ToPoint(i - 1, points[i - 1]);
            var current = ToPoint(i, points[i]);
            canvas.DrawLine(previous.X, previous.Y, current.X, current.Y);
        }
        canvas.FillColor = Color.FromArgb("#80CBC4");
        foreach (var (point, index) in points.Select((point, index) => (point, index)))
        {
            var position = ToPoint(index, point);
            canvas.FillCircle(position.X, position.Y, 4);
        }
        canvas.FontColor = Color.FromArgb("#BDBDBD");
        canvas.FontSize = 10;
        canvas.DrawString(points[0].Date.ToString("M/d"), new RectF(plot.Left, plot.Bottom + 5, 55, 16), HorizontalAlignment.Left, VerticalAlignment.Center);
        canvas.DrawString(points[^1].Date.ToString("M/d"), new RectF(plot.Right - 55, plot.Bottom + 5, 55, 16), HorizontalAlignment.Right, VerticalAlignment.Center);
    }
}
