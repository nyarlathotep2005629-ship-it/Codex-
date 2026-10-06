using System.Globalization;
using System.Windows.Data;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.App.Converters;

public sealed class UiDisplayTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => ToJapanese(value);
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    public static string ToJapanese(object? value) => value switch
    {
        DashboardPeriod.OneMonth => "1か月",
        DashboardPeriod.ThreeMonths => "3か月",
        DashboardPeriod.SixMonths => "6か月",
        DashboardPeriod.OneYear => "1年",
        DashboardPeriod.All => "全期間",
        StatisticsMetric.EstimatedOneRepMax => "推定1RM",
        StatisticsMetric.MaxWeight => "最大重量",
        StatisticsMetric.Volume => "ボリューム",
        StatisticsMetric.Sets => "セット数",
        StatisticsMetric.Reps => "レップ数",
        ThemeMode.System => "システム設定に合わせる",
        ThemeMode.Light => "ライト",
        ThemeMode.Dark => "ダーク",
        string text => text switch
        {
            "Dashboard" => "ダッシュボード",
            "Workout" => "ワークアウト",
            "History" => "履歴",
            "Statistics" => "統計",
            "Settings" => "設定",
            "WorkoutCount" or "Workout Count" => "ワークアウト回数",
            "TotalVolume" or "Total Volume" => "総ボリューム",
            "TotalSets" or "Total Sets" => "総セット数",
            _ => text
        },
        null => string.Empty,
        _ => value.ToString() ?? string.Empty
    };
}
