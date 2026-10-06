using System.Windows;
using System.Windows.Controls;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;
using System.Windows.Media;

namespace WorkoutTracker.App.Views;

public sealed record ScottPlotRenderPlan(string Title, string Unit, double[] X, double[] Y, bool IsEmpty, double? MinimumY, double? MaximumY);

public static class ScottPlotChartAdapter
{
    public const string JapaneseFontName = "Yu Gothic UI";

    // WPF formats colors as AARRGGBB; ScottPlot hex strings use RRGGBBAA.
    // Passing channels explicitly also preserves alpha without swapping them.
    public static ScottPlot.Color ToPlotColor(Color color) => new(color.R, color.G, color.B, color.A);

    public static ScottPlotRenderPlan CreatePlan(ChartSeriesDto? series)
    {
        if (series is null || series.Points.Count == 0) return new(series?.ExerciseName ?? string.Empty, series?.Unit ?? string.Empty, [], [], true, null, null);
        var x = series.Points.Select(point => point.Date.ToDateTime(TimeOnly.MinValue).ToOADate()).ToArray();
        var y = series.Points.Select(point => (double)point.Value).ToArray();
        return new(series.ExerciseName, series.Unit, x, y, false, y.Min(), y.Max());
    }

    public static IReadOnlyList<ScottPlotRenderPlan> CreateSmallMultiples(IReadOnlyList<ChartSeriesDto> series)
    {
        if (series.Count > 4) throw new ArgumentOutOfRangeException(nameof(series));
        return series.Select(CreatePlan).ToList();
    }

    public static int Render(ScottPlot.Plot plot, ChartSeriesDto? series, ScottPlot.Color? seriesColor = null)
    {
        plot.Clear();
        plot.Font.Set(JapaneseFontName);
        plot.Legend.FontName = JapaneseFontName;
        plot.Legend.SetBestFontOnEachRender = false;
        var plan = CreatePlan(series); if (plan.IsEmpty) return 0;
        var scatter = plot.Add.Scatter(plan.X, plan.Y); scatter.LegendText = plan.Title; scatter.MarkerSize = 7;
        if (seriesColor.HasValue) scatter.Color = seriesColor.Value;
        plot.Axes.DateTimeTicksBottom(); plot.Axes.Bottom.Label.Text = "日付"; plot.Axes.Left.Label.Text = $"{plan.Title} ({plan.Unit})"; plot.ShowLegend(); plot.Axes.AutoScale();
        return plan.X.Length;
    }
}

public partial class AnalyticsChartView : UserControl
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(ChartSeriesDto), typeof(AnalyticsChartView), new PropertyMetadata(null, OnSeriesChanged));

    public AnalyticsChartView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }
    public ChartSeriesDto? Series { get => (ChartSeriesDto?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public int PlotInstanceCount => Series is null ? 0 : 1;

    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AnalyticsChartView)d).Render();

    private void OnLoaded(object sender, RoutedEventArgs e) { ThemeService.GlobalThemeChanged += OnThemeChanged; Render(); }
    private void OnUnloaded(object sender, RoutedEventArgs e) => ThemeService.GlobalThemeChanged -= OnThemeChanged;
    private void OnThemeChanged(object? sender, EventArgs e) => Render();

    private void Render()
    {
        if (!IsLoaded) return;
        var series = Series;
        EmptyOverlay.Visibility = series is null || series.Points.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        var accent = ((SolidColorBrush)FindResource("AccentBrush")).Color;
        ScottPlotChartAdapter.Render(PlotControl.Plot, series, ScottPlotChartAdapter.ToPlotColor(accent));
        ApplyTheme();
        PlotControl.ToolTip = series is null ? null : string.Join(Environment.NewLine, series.Points.Select(point => point.Tooltip).Where(x => !string.IsNullOrWhiteSpace(x)));
        PlotControl.Refresh();
    }

    private void ApplyTheme()
    {
        var surface = ScottPlotChartAdapter.ToPlotColor(((SolidColorBrush)FindResource("SurfaceBrush")).Color);
        var text = ScottPlotChartAdapter.ToPlotColor(((SolidColorBrush)FindResource("TextPrimaryBrush")).Color);
        var border = ScottPlotChartAdapter.ToPlotColor(((SolidColorBrush)FindResource("BorderBrush")).Color);
        var plot = PlotControl.Plot;
        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;
        plot.Axes.Color(text);
        plot.Axes.DefaultGrid.MajorLineColor = border;
        plot.Axes.DefaultGrid.MinorLineColor = border;
        plot.Legend.FontColor = text;
        plot.Legend.BackgroundColor = surface;
        plot.Legend.OutlineColor = border;
    }
}
