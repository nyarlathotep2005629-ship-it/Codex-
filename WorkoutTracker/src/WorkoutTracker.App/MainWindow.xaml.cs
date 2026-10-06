using System.Windows;
using System.ComponentModel;
using WorkoutTracker.Application;
using WorkoutTracker.App.ViewModels;

namespace WorkoutTracker.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly ShutdownCoordinator _shutdown;
    private readonly ActiveWorkoutState _activeState;
    private bool _allowClose;
    public MainWindow(ShellViewModel viewModel, ShutdownCoordinator shutdown, ActiveWorkoutState activeState)
    {
        InitializeComponent(); _viewModel = viewModel; _shutdown = shutdown; _activeState = activeState; DataContext = viewModel;
    }
    private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.InitializeAsync();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !_activeState.IsActive || !_activeState.HasUnsavedChanges) return;
        e.Cancel = true;
        while (true)
        {
            var result = await _shutdown.FlushPendingDraftAsync();
            if (result is ShutdownFlushResult.Saved or ShutdownFlushResult.NotRequired)
            {
                _allowClose = true; Close(); return;
            }
        var choice = MessageBox.Show("下書きの保存を完了できませんでした。\nはい: 再試行 / いいえ: 最後に保存済みの状態までで終了 / キャンセル: 終了を中止", "終了時の保存", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice == MessageBoxResult.Yes) continue;
            if (choice == MessageBoxResult.No) { _allowClose = true; Close(); }
            return;
        }
    }
}
