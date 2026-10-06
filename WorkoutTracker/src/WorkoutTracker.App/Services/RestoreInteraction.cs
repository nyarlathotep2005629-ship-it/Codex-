using Microsoft.Win32;
using System.Windows;

namespace WorkoutTracker.App.Services;

public interface IRestoreInteraction
{
    string? SelectBackupFile();
    bool ConfirmRestore(string selectedBackupPath);
}

public sealed class RestoreInteraction : IRestoreInteraction
{
    public string? SelectBackupFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "復元するWorkout Trackerバックアップを選択",
            Filter = "Workout Trackerデータベース (*.db)|*.db|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool ConfirmRestore(string selectedBackupPath) => MessageBox.Show(
        $"現在のWorkout Trackerデータは、選択したバックアップの内容へ置き換えられます。\n\n置換直前に現在のデータベースの緊急バックアップを作成します。\n選択: {selectedBackupPath}\n\n復元を実行しますか？",
        "復元の確認",
        MessageBoxButton.YesNo,
        MessageBoxImage.Warning,
        MessageBoxResult.No) == MessageBoxResult.Yes;
}
