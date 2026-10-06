using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WorkoutTracker.App.ViewModels;

namespace WorkoutTracker.App.Views;

public partial class WorkoutView : UserControl
{
    public static DispatcherPriority FocusDispatcherPriority => DispatcherPriority.Loaded;
    private bool _applyingFocus;
    public WorkoutView() { InitializeComponent(); DataContextChanged += OnDataContextChanged; }
    private ActiveWorkoutViewModel? ViewModel => DataContext as ActiveWorkoutViewModel;
    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ActiveWorkoutViewModel oldVm) oldVm.FocusRequested -= FocusRequested;
        if (e.NewValue is ActiveWorkoutViewModel newVm) newVm.FocusRequested += FocusRequested;
    }
    private async void FocusRequested(object? sender, WorkoutFocusRequest request)
    {
        var focusedAtRequest = Keyboard.FocusedElement;
        await Dispatcher.InvokeAsync(() => { }, FocusDispatcherPriority);
        var vm = ViewModel; if (vm is null || !vm.IsCurrentFocusRequest(request.RequestId) || Keyboard.FocusedElement != focusedAtRequest) return;
        var target = FindTarget(this, request); if (target is null) return;
        _applyingFocus = true;
        try { target.BringIntoView(); target.Focus(); Keyboard.Focus(target); }
        finally { _applyingFocus = false; }
    }
    private static Control? FindTarget(DependencyObject root, WorkoutFocusRequest request)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Control control)
            {
                if (request.Field == WorkoutFocusField.AddSet && control is Button { Name: "AddSetButton", DataContext: WorkoutExerciseViewModel exercise } && exercise.WorkoutExerciseId == request.WorkoutExerciseId) return control;
                if (request.WorkoutSetId.HasValue && control.DataContext is WorkoutSetViewModel set && set.WorkoutSetId == request.WorkoutSetId && control.Name == request.Field + "Input") return control;
            }
            var found = FindTarget(child, request); if (found is not null) return found;
        }
        return null;
    }
    private void OnPreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { if (!_applyingFocus) ViewModel?.InvalidateFocusRequest(); }
    private void SetInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { DataContext: WorkoutSetViewModel set, Tag: string field }) return;
        set.HandleEnter(Enum.Parse<WorkoutFocusField>(field)); e.Handled = true;
    }
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control || Keyboard.FocusedElement is not FrameworkElement { DataContext: WorkoutSetViewModel set }) return;
        var exercise = ViewModel?.Exercises.SingleOrDefault(x => x.Sets.Contains(set)); if (exercise is null) return;
        ViewModel!.AddSet(exercise); e.Handled = true;
    }
    private async void StartWithoutMenu_Click(object sender, RoutedEventArgs e) { if (ViewModel is null) return; try { await ViewModel.StartAsync(null, DateOnly.FromDateTime(DateTime.Today)); } catch { ViewModel.ReportOperationError("ワークアウトを開始できませんでした。"); } }
    private async void StartMenu_Click(object sender, RoutedEventArgs e) { if (ViewModel is null || sender is not Button { Tag: Guid menuId }) return; try { await ViewModel.StartAsync(menuId, DateOnly.FromDateTime(DateTime.Today)); } catch { ViewModel.ReportOperationError("ワークアウトを開始できませんでした。"); } }
    private async void Discard_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        var confirmed = MessageBox.Show("この下書きワークアウトを破棄しますか？", "ワークアウトの破棄", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        await ViewModel.DiscardAsync(confirmed);
    }
    private void RemoveExercise_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button { DataContext: WorkoutExerciseViewModel exercise }) return;
        if (MessageBox.Show($"{exercise.ExerciseName}をこのワークアウトから削除しますか？", "種目の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) ViewModel.RemoveExercise(exercise);
    }
    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null) return;
        if (HasBindingError(this)) { ViewModel.ReportOperationError("入力エラーを修正してください。"); return; }
        await ViewModel.FinishAsync();
    }
    private static bool HasBindingError(DependencyObject root)
    {
        if (root is FrameworkElement element && Validation.GetHasError(element)) return true;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (HasBindingError(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }
}
