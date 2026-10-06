using Microsoft.Win32;
using System.Windows;
using WorkoutTracker.Application;

namespace WorkoutTracker.App.Services;

public interface ITransferInteraction
{
    string? SelectExportPath(string suggestedFileName);
    string? SelectImportPath();
    bool ConfirmImport(ImportPlan plan);
    bool? ResolveConflict(TransferConflict conflict);
}

public sealed class TransferInteraction : ITransferInteraction
{
    public string? SelectExportPath(string suggestedFileName)
    {
        var dialog = new SaveFileDialog { Title = "Workout Trackerデータをエクスポート", FileName = suggestedFileName, Filter = "Workout Tracker Transfer (*.wtb)|*.wtb", DefaultExt = ".wtb", AddExtension = true, OverwritePrompt = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SelectImportPath()
    {
        var dialog = new OpenFileDialog { Title = "Workout Trackerデータをインポート", Filter = "Workout Tracker Transfer (*.wtb)|*.wtb", CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool ConfirmImport(ImportPlan plan) => MessageBox.Show(
        $"転送ファイルを検証しました。\n\n追加: {plan.NewItems.Count}\n変更なし: {plan.UnchangedItems.Count}\n競合: {plan.Conflicts.Count}\n\n競合解決後にインポートを実行します。続行しますか？",
        "Workout Trackerデータのインポート確認", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;

    public bool? ResolveConflict(TransferConflict conflict) => MessageBox.Show(
        $"競合を検出しました。\n\n種別: {conflict.Kind}\n対象: {conflict.Label}\n\n[はい] インポート版を使用\n[いいえ] ローカルを保持\n[キャンセル] インポート中止",
        "Workout Trackerデータの競合", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning) switch
    {
        MessageBoxResult.Yes => true,
        MessageBoxResult.No => false,
        _ => null
    };
}
