using Microsoft.Win32;
using System.Windows;
using ThemeMode = WorkoutTracker.Domain.ThemeMode;

namespace WorkoutTracker.App.Services;

public sealed class ThemeService : IDisposable
{
    private readonly Func<ThemeMode> _systemThemeReader;
    private ThemeMode _mode = ThemeMode.System;
    private bool _listening;
    public ThemeMode Mode => _mode;
    public ThemeMode EffectiveMode { get; private set; } = ThemeMode.Light;
    public event EventHandler? ThemeChanged;
    public static event EventHandler? GlobalThemeChanged;

    public ThemeService(Func<ThemeMode>? systemThemeReader = null) => _systemThemeReader = systemThemeReader ?? ReadSystemTheme;

    public void Apply(ThemeMode mode)
    {
        _mode = mode;
        if (!_listening)
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listening = true;
        }
        ApplyEffective(mode == ThemeMode.System ? _systemThemeReader() : mode);
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_mode != ThemeMode.System) return;
        System.Windows.Application.Current.Dispatcher.BeginInvoke(RefreshSystemTheme);
    }

    public void RefreshSystemTheme()
    {
        if (_mode == ThemeMode.System) ApplyEffective(_systemThemeReader());
    }

    private void ApplyEffective(ThemeMode effective)
    {
        if (effective is not (ThemeMode.Light or ThemeMode.Dark)) effective = ThemeMode.Light;
        EffectiveMode = effective;
        var application = System.Windows.Application.Current;
#pragma warning disable WPF0001 // .NET 10 Fluent theme keeps standard WPF control templates aligned with the resolved app theme.
        application.ThemeMode = effective == ThemeMode.Dark
            ? System.Windows.ThemeMode.Dark
            : System.Windows.ThemeMode.Light;
#pragma warning restore WPF0001
        var dictionaries = application.Resources.MergedDictionaries;
        var previous = dictionaries.FirstOrDefault(IsThemeDictionary);
        var replacement = new ResourceDictionary { Source = new Uri($"/WorkoutTracker.App;component/Themes/{effective}.xaml", UriKind.Relative) };
        if (previous is null) dictionaries.Insert(0, replacement); else dictionaries[dictionaries.IndexOf(previous)] = replacement;
        foreach (var duplicate in dictionaries.Where(IsThemeDictionary).Where(x => !ReferenceEquals(x, replacement)).ToList())
            dictionaries.Remove(duplicate);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
        GlobalThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        if (source is null) return false;
        return source.EndsWith("Themes/Light.xaml", StringComparison.OrdinalIgnoreCase)
            || source.EndsWith("Themes/Dark.xaml", StringComparison.OrdinalIgnoreCase);
    }

    private static ThemeMode ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0 ? ThemeMode.Dark : ThemeMode.Light;
        }
        catch { return ThemeMode.Light; }
    }

    public void Dispose()
    {
        if (_listening) SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _listening = false;
    }
}
