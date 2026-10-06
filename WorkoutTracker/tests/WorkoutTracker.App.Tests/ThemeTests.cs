using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ScottPlot.WPF;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;
using WorkoutTracker.App.Views;
using WorkoutTracker.App.Converters;
using ThemeMode = WorkoutTracker.Domain.ThemeMode;

namespace WorkoutTracker.App.Tests;

public sealed class ThemeTests
{
    private static readonly string[] RequiredBrushes =
    [
        "AppBackgroundBrush", "SurfaceBrush", "SurfaceAltBrush", "TextPrimaryBrush", "TextSecondaryBrush",
        "AccentBrush", "AccentHoverBrush", "AccentTextBrush", "SuccessBrush", "SuccessSurfaceBrush",
        "WarningBrush", "WarningSurfaceBrush", "ErrorBrush", "ErrorSurfaceBrush", "DangerBrush",
        "DangerHoverBrush", "DangerTextBrush", "BorderBrush",
        "FocusBrush", "NavigationBackgroundBrush", "NavigationTextBrush", "NavigationItemBrush",
        "NavigationItemHoverBrush", "NavigationSelectedBrush", "NavigationAccentBrush",
        "SelectionBackgroundBrush", "SelectionTextBrush",
        "DisabledBackgroundBrush", "DisabledTextBrush",
        "SelectorBackgroundBrush", "SelectorForegroundBrush", "SelectorBorderBrush", "SelectorHoverBackgroundBrush",
        "SelectorSelectedBackgroundBrush", "SelectorSelectedForegroundBrush", "SelectorDisabledBackgroundBrush", "SelectorDisabledForegroundBrush"
    ];

    [Fact(DisplayName = "THM-001 THM-002 Light Dark System replace resource dictionaries immediately")]
    public async Task Thm001Thm002()
    {
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                WpfTestApplication.Run(app =>
                {
                    Window? host = null;
                    try
                    {
                    var systemTheme = ThemeMode.Light;
                    using var service = new ThemeService(() => systemTheme);
                    service.Apply(ThemeMode.Light);
                    var chart = new AnalyticsChartView
                    {
                        Series = new ChartSeriesDto(Guid.NewGuid(), "Bench Press", "kg", [new(new DateOnly(2026, 8, 23), 100m)])
                    };
                    host = new Window { Content = chart, Width = 500, Height = 300, ShowInTaskbar = false, Opacity = 0 };
                    host.Show();

                    Assert.Equal(Color.FromRgb(0xF4, 0xF7, 0xFA), ((SolidColorBrush)app.Resources["AppBackgroundBrush"]).Color);
                    AssertEffectiveResources(app, ThemeMode.Light);
                    AssertSharedUiResources(app);
                    AssertSelectorTemplate(app);
#pragma warning disable WPF0001
                    Assert.Equal(System.Windows.ThemeMode.Light, app.ThemeMode);
#pragma warning restore WPF0001
                    var plot = Assert.IsType<WpfPlot>(chart.FindName("PlotControl")).Plot;
                    Assert.Equal(ScottPlotColor(app, "SurfaceBrush"), plot.FigureBackground.Color.ToHex());

                    service.Apply(ThemeMode.Dark);
                    Assert.Equal(Color.FromRgb(0x08, 0x13, 0x20), ((SolidColorBrush)app.Resources["AppBackgroundBrush"]).Color);
                    AssertEffectiveResources(app, ThemeMode.Dark);
                    AssertSelectorTemplate(app);
#pragma warning disable WPF0001
                    Assert.Equal(System.Windows.ThemeMode.Dark, app.ThemeMode);
#pragma warning restore WPF0001
                    Assert.Equal(ScottPlotColor(app, "SurfaceBrush"), plot.FigureBackground.Color.ToHex());

                    service.Apply(ThemeMode.System);
                    Assert.Equal(ThemeMode.Light, service.EffectiveMode);
                    AssertEffectiveResources(app, ThemeMode.Light);
                    systemTheme = ThemeMode.Dark; service.RefreshSystemTheme();
                    Assert.Equal(ThemeMode.Dark, service.EffectiveMode);
                    AssertEffectiveResources(app, ThemeMode.Dark);
                    host.Close(); host = null;
                    }
                    finally { host?.Close(); }
                });
                result.SetResult(null);
            }
            catch (Exception ex) { result.SetResult(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        var error = await result.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Null(error);
    }

    [Fact(DisplayName = "UI-001 Japanese display converter keeps only accepted training abbreviations")]
    public void Ui001JapaneseDisplayConverter()
    {
        var converter = new UiDisplayTextConverter();
        Assert.Equal("ダッシュボード", converter.Convert("Dashboard", typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("推定1RM", converter.Convert(StatisticsMetric.EstimatedOneRepMax, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("システム設定に合わせる", converter.Convert(ThemeMode.System, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal("ダーク", converter.Convert(ThemeMode.Dark, typeof(string), null!, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact(DisplayName = "UI-R2-001 WPF ARGB channels reach ScottPlot without rotation")]
    public void PlotColorPreservesChannels()
    {
        foreach (var c in new[] { Color.FromRgb(0x11, 0x1A, 0x2B), Color.FromArgb(0x42, 0x22, 0xD3, 0xEE) })
        {
            var actual = ScottPlotChartAdapter.ToPlotColor(c);
            Assert.Equal(c.R, actual.R); Assert.Equal(c.G, actual.G); Assert.Equal(c.B, actual.B); Assert.Equal(c.A, actual.A);
        }
    }

    [Fact(DisplayName = "UI-R2-002 Japanese chart labels use an installed Japanese font on every render")]
    public void JapaneseChartFont()
    {
        using var plot = new ScottPlot.Plot();
        var series = new ChartSeriesDto(Guid.NewGuid(), "ベンチプレス", "kg", [new(new DateOnly(2026, 8, 31), 140m)]);
        Assert.Equal(1, ScottPlotChartAdapter.Render(plot, series));
        Assert.Equal(ScottPlotChartAdapter.JapaneseFontName, plot.Axes.Left.Label.FontName);
        Assert.Equal(ScottPlotChartAdapter.JapaneseFontName, plot.Axes.Bottom.Label.FontName);
        Assert.Equal(ScottPlotChartAdapter.JapaneseFontName, plot.Legend.FontName);
        var typeface = ScottPlot.Fonts.GetTypeface(ScottPlotChartAdapter.JapaneseFontName, false, false);
        Assert.True(typeface.ContainsGlyphs("日付ベンチプレス推定最大重量回セット"));
        Assert.Equal(0, ScottPlotChartAdapter.Render(plot, null));
        Assert.Equal(1, ScottPlotChartAdapter.Render(plot, series));
        Assert.Equal(ScottPlotChartAdapter.JapaneseFontName, plot.Legend.FontName);
    }

    private static void AssertSelectorTemplate(System.Windows.Application app)
    {
        var combo = new ComboBox { ItemsSource = new[] { "ベンチプレス", "デッドリフト" }, SelectedIndex = 0, Width = 260 };
        combo.Style = (Style)app.FindResource(typeof(ComboBox));
        combo.ApplyTemplate();
        var chrome = Assert.IsType<ToggleButton>(combo.Template.FindName("DropDownToggle", combo));
        Assert.Equal(((SolidColorBrush)app.FindResource("SelectorBackgroundBrush")).Color, ((SolidColorBrush)chrome.Background).Color);
        Assert.Equal(((SolidColorBrush)app.FindResource("SelectorForegroundBrush")).Color, ((SolidColorBrush)chrome.Foreground).Color);
        var popup = Assert.IsType<Border>(combo.Template.FindName("PopupBorder", combo));
        Assert.Equal(((SolidColorBrush)app.FindResource("SelectorBackgroundBrush")).Color, ((SolidColorBrush)popup.Background).Color);
        combo.IsEnabled = false;
        Assert.Equal(((SolidColorBrush)app.FindResource("SelectorDisabledForegroundBrush")).Color, ((SolidColorBrush)combo.Foreground).Color);
        AssertContrast(app, "SelectorForegroundBrush", "SelectorBackgroundBrush", 4.5);
        AssertContrast(app, "SelectorForegroundBrush", "SelectorHoverBackgroundBrush", 4.5);
        AssertContrast(app, "SelectorSelectedForegroundBrush", "SelectorSelectedBackgroundBrush", 4.5);
        AssertContrast(app, "SelectorDisabledForegroundBrush", "SelectorDisabledBackgroundBrush", 4.5);
    }

    private static void AssertSharedUiResources(System.Windows.Application app)
    {
        foreach (var key in new[] { "Space4", "Space8", "Space12", "Space16", "Space24", "Space32", "PagePadding", "CardPadding", "CardCornerRadius" })
            Assert.NotNull(app.Resources[key]);
        foreach (var key in new[] { "PageTitleStyle", "SectionTitleStyle", "CardTitleStyle", "PrimaryMetricStyle", "BodyTextStyle", "CaptionTextStyle", "InlineValidationStyle", "CardStyle", "InteractiveCardStyle", "SelectedCardStyle", "EmptyStateCardStyle", "ErrorStateCardStyle", "PrimaryButtonStyle", "SecondaryButtonStyle", "DangerButtonStyle" })
            Assert.IsType<Style>(app.Resources[key]);
        Assert.Equal("ダッシュボード", app.Resources["NavDashboard"]);
        Assert.Equal("再試行", app.Resources["ActionRetry"]);
    }

    private static void AssertEffectiveResources(System.Windows.Application app, ThemeMode expected)
    {
        var customTheme = Assert.Single(app.Resources.MergedDictionaries, x =>
            x.Source?.OriginalString.EndsWith("/Light.xaml", StringComparison.OrdinalIgnoreCase) == true
            || x.Source?.OriginalString.EndsWith("/Dark.xaml", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains($"/{expected}.xaml", customTheme.Source!.OriginalString, StringComparison.OrdinalIgnoreCase);
        foreach (var key in RequiredBrushes) Assert.IsType<SolidColorBrush>(app.Resources[key]);

        AssertContrast(app, "TextPrimaryBrush", "AppBackgroundBrush", 7.0);
        AssertContrast(app, "TextPrimaryBrush", "SurfaceBrush", 7.0);
        AssertContrast(app, "TextSecondaryBrush", "AppBackgroundBrush", 4.5);
        AssertContrast(app, "NavigationTextBrush", "NavigationItemBrush", 4.5);
        AssertContrast(app, "SelectionTextBrush", "SelectionBackgroundBrush", 4.5);
        AssertContrast(app, "DisabledTextBrush", "DisabledBackgroundBrush", 3.0);
    }

    private static void AssertContrast(System.Windows.Application app, string foregroundKey, string backgroundKey, double minimum)
    {
        var foreground = ((SolidColorBrush)app.Resources[foregroundKey]).Color;
        var background = ((SolidColorBrush)app.Resources[backgroundKey]).Color;
        var ratio = Contrast(foreground, background);
        Assert.True(ratio >= minimum, $"{foregroundKey}/{backgroundKey} contrast {ratio:0.00} is below {minimum:0.0}.");
    }

    private static string ScottPlotColor(System.Windows.Application app, string resourceKey) =>
        ((SolidColorBrush)app.Resources[resourceKey]).Color is var c ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : throw new InvalidOperationException();

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255d;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        }
        var firstLuminance = Luminance(first);
        var secondLuminance = Luminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05) / (Math.Min(firstLuminance, secondLuminance) + 0.05);
    }
}
