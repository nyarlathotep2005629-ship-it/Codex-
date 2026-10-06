using System.Windows.Controls;
using System.Windows.Input;
using WorkoutTracker.App.ViewModels;

namespace WorkoutTracker.App.Views;

public partial class HistoryView : UserControl
{
    public HistoryView() => InitializeComponent();
    private void HistoryView_MouseDoubleClick(object sender, MouseButtonEventArgs e) { if (DataContext is HistoryViewModel vm && HistoryList.SelectedItem is HistoryItemViewModel item && vm.OpenDetailCommand.CanExecute(item)) vm.OpenDetailCommand.Execute(item); }
}
