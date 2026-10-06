using System.Windows;
using System.Windows.Threading;

namespace WorkoutTracker.App.Tests;

internal static class WpfTestApplication
{
    private static readonly object Gate = new();
    private static Host? _host;

    public static void Run(Action<System.Windows.Application> action)
    {
        var host = GetOrCreate();
        host.Dispatcher.Invoke(() => action(host.Application));
    }

    public static Task RunAsync(Func<System.Windows.Application, Task> action)
    {
        var host = GetOrCreate();
        return host.Dispatcher.InvokeAsync(() => action(host.Application)).Task.Unwrap();
    }

    private static Host GetOrCreate()
    {
        lock (Gate)
        {
            if (_host is not null) return _host;

            var ready = new TaskCompletionSource<Host>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    var application = new System.Windows.Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    AddDictionary(application, "Themes/Light.xaml");
                    AddDictionary(application, "Resources/Strings.ja.xaml");
                    AddDictionary(application, "Themes/SharedStyles.xaml");
                    ready.SetResult(new(application, Dispatcher.CurrentDispatcher));
                    Dispatcher.Run();
                }
                catch (Exception ex) { ready.SetException(ex); }
            })
            {
                IsBackground = true,
                Name = "WorkoutTracker.WpfTestApplication"
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            _host = ready.Task.GetAwaiter().GetResult();
            return _host;
        }
    }

    private static void AddDictionary(System.Windows.Application application, string source) =>
        application.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri($"/WorkoutTracker.App;component/{source}", UriKind.Relative)
        });

    private sealed record Host(System.Windows.Application Application, Dispatcher Dispatcher);
}
