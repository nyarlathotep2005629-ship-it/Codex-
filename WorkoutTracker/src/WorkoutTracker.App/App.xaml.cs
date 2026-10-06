using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Windows;
using WorkoutTracker.Application;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Services;
using WorkoutTracker.Infrastructure;
using WorkoutTracker.Infrastructure.Persistence;
using System.Windows.Threading;

namespace WorkoutTracker.App;

public partial class App : System.Windows.Application
{
    private IHost? _host;
    private readonly FatalErrorGate _fatalErrorGate = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        var databaseDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WorkoutTracker");
        var databasePath = Path.Combine(databaseDirectory, "workout-tracker.db");

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddApplication();
                services.AddInfrastructure(databasePath);
                services.AddSingleton<ActiveWorkoutViewModel>();
                services.AddSingleton<MasterDataViewModel>();
                services.AddSingleton<HistoryViewModel>();
                services.AddSingleton<CalendarViewModel>();
                services.AddSingleton<DashboardViewModel>();
                services.AddSingleton<StatisticsViewModel>();
                services.AddSingleton<IApplicationStateReloader, WpfApplicationStateReloader>();
                services.AddSingleton<ThemeService>();
                services.AddSingleton<IUiExceptionReporter>(_ => new LocalFileUiExceptionReporter(Path.Combine(databaseDirectory, "Logs")));
                services.AddSingleton<IRestoreInteraction, RestoreInteraction>();
                services.AddSingleton<ITransferInteraction, TransferInteraction>();
                services.AddSingleton<SettingsViewModel>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<MainWindow>();
            })
            .Build();

        try
        {
            await _host.StartAsync();
            await _host.Services.GetRequiredService<IDatabaseInitializer>().InitializeFreshDatabaseAsync();
            await _host.Services.GetRequiredService<SettingsViewModel>().InitializeAsync();
            var shell = _host.Services.GetRequiredService<ShellViewModel>();
            await shell.InitializeAsync();
            if (shell.HasActiveWorkout)
            {
                while (shell.HasActiveWorkout)
                {
                    var choice = MessageBox.Show("保存済みの下書きワークアウトがあります。再開しますか？\n「いいえ」を選ぶと破棄確認へ進みます。", "ワークアウトを再開", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (choice == MessageBoxResult.Yes) { shell.ContinueActiveWorkout(); break; }
                    if (MessageBox.Show("下書きワークアウトを完全に破棄しますか？", "破棄の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes && await shell.DiscardActiveWorkoutAsync()) break;
                }
            }
            _host.Services.GetRequiredService<MainWindow>().Show();
        }
        catch (StartupRecoveryException ex)
        {
            MessageBox.Show(ex.ToSafeUserMessage(), "起動時の復旧", MessageBoxButton.OK, ex.IsFatal ? MessageBoxImage.Error : MessageBoxImage.Warning);
            Shutdown(-1);
        }
        catch (Exception ex)
        {
            var correlationId = Guid.NewGuid();
            _host?.Services.GetService<ILogger<App>>()?.LogCritical(ex, "Startup failed. Correlation ID: {CorrelationId}", correlationId);
            MessageBox.Show($"データベースを安全に起動できませんでした。通常画面は開きません。\n\n問い合わせID: {correlationId}", "起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Services.GetService<ThemeService>()?.Dispose();
            _host.Dispose();
        }

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        if (!_fatalErrorGate.TryEnter()) return;
        var correlationId = Guid.NewGuid();
        _host?.Services.GetService<IUiExceptionReporter>()?.Report(e.Exception, "DispatcherUnhandledException", correlationId);
        MessageBox.Show($"予期しないエラーが発生したため通常操作を停止します。最後に保存済みの下書きは次回起動時に復旧できます。\n\n問い合わせID: {correlationId}", "予期しないエラー", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(-1);
    }
}
