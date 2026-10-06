using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.Application;

namespace WorkoutTracker.App.Views;

public partial class MasterDataView : UserControl
{
    public const string MenuDeleteExplanation = "進行中のトレーニング記録には影響しません。";
    private Point _dragStart;
    private MenuExerciseEditorViewModel? _draggedItem;
    public MasterDataView() => InitializeComponent();
    private MasterDataViewModel? ViewModel => DataContext as MasterDataViewModel;

    private async void StartMenu_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null && sender is Button { Tag: MenuMasterItemViewModel menu }) await ViewModel.StartWorkoutAsync(menu); }
    private void EditMenu_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null && sender is Button { Tag: MenuMasterItemViewModel menu }) ViewModel.SelectedMenu = menu; }
    private void CreateExerciseFromMenu_Click(object sender, RoutedEventArgs e) { ViewModel?.NewExerciseCommand.Execute(null); MasterTabs.SelectedIndex = 1; }
    private async void DuplicateMenu_Click(object sender, RoutedEventArgs e) { if (ViewModel is not null && sender is Button { Tag: MenuMasterItemViewModel menu }) await ViewModel.DuplicateMenuAsync(menu); }
    private async void DeleteMenu_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel is null || sender is not Button { Tag: MenuMasterItemViewModel menu }) return;
        var text = $"メニュー「{menu.Name}」を削除しますか？\n{MenuDeleteExplanation}";
        if (MessageBox.Show(text, "メニューの削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) await ViewModel.DeleteMenuAsync(menu);
    }
    private async void ToggleExercise_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedExercise is not { } exercise) return;
        var next = !exercise.IsActive;
        if (!next && MessageBox.Show("この種目を非表示にしますか？過去のワークアウト記録は保持されます。", "種目を非表示", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await ViewModel.SetSelectedExerciseActiveAsync(next);
    }
    private async void DeleteExercise_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedExercise is null || MessageBox.Show("未使用の種目を完全に削除しますか？この操作は元に戻せません。", "種目の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (await ViewModel.DeleteSelectedExerciseAsync() == ExerciseDeleteResult.ReferencedMustHide) MessageBox.Show("使用済み、またはメニューで使用中のため削除できません。非表示を使用してください。", "種目を削除できません", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private async void DeleteMuscleGroup_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel?.SelectedMuscleGroup is null || MessageBox.Show("この部位を削除しますか？", "部位の削除", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        if (await ViewModel.DeleteSelectedMuscleGroupAsync() == MuscleGroupDeleteResult.Referenced) MessageBox.Show("現在の種目でメイン部位・サブ部位として使用中のため削除できません。", "部位を削除できません", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void MenuExerciseList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) { _dragStart = e.GetPosition(MenuExerciseList); _draggedItem = FindItem(e.OriginalSource as DependencyObject); }
    private void MenuExerciseList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem is null) return; var point = e.GetPosition(MenuExerciseList);
        if (Math.Abs(point.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(point.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        DragDrop.DoDragDrop(MenuExerciseList, _draggedItem, DragDropEffects.Move);
    }
    private void MenuExerciseList_Drop(object sender, DragEventArgs e)
    {
        if (ViewModel is null || !e.Data.GetDataPresent(typeof(MenuExerciseEditorViewModel))) return;
        var source = (MenuExerciseEditorViewModel)e.Data.GetData(typeof(MenuExerciseEditorViewModel))!; var target = FindItem(e.OriginalSource as DependencyObject);
        if (target is not null) ViewModel.MoveMenuExercise(ViewModel.MenuExercises.IndexOf(source), ViewModel.MenuExercises.IndexOf(target));
        _draggedItem = null;
    }
    private static MenuExerciseEditorViewModel? FindItem(DependencyObject? current)
    {
        while (current is not null) { if (current is FrameworkElement { DataContext: MenuExerciseEditorViewModel item }) return item; current = System.Windows.Media.VisualTreeHelper.GetParent(current); } return null;
    }
}
