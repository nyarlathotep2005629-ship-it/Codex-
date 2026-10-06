using System.Reflection;
using System.Windows.Input;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;
using WorkoutTracker.Domain;

namespace WorkoutTracker.App.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly BackupService _backup;
    private readonly ThemeService _theme;
    private readonly RestoreService _restore;
    private readonly IRestoreInteraction _restoreInteraction;
    private readonly IUiExceptionReporter _errors;
    private readonly ITransferService _transfer;
    private readonly ITransferInteraction _transferInteraction;
    private ThemeMode _selectedTheme;
    private bool _initialized;
    private bool _isBusy;
    private string _databasePath = string.Empty;
    private string _backupDirectory = string.Empty;
    private string _statusMessage = string.Empty;
    private string? _errorMessage;
    private string? _lastBackupPath;
    private string? _selectedRestorePath;
    private string? _restoreValidation;
    private string? _lastEmergencyBackupPath;

    public SettingsViewModel(SettingsService settings, BackupService backup, ThemeService theme, RestoreService restore, IRestoreInteraction restoreInteraction, ITransferService transfer, ITransferInteraction transferInteraction, IUiExceptionReporter? errors = null)
    {
        _settings = settings; _backup = backup; _theme = theme; _restore = restore; _restoreInteraction = restoreInteraction; _transfer = transfer; _transferInteraction = transferInteraction; _errors = errors ?? NullUiExceptionReporter.Instance;
        BackupCommand = new AsyncRelayCommand(CreateBackupAsync, () => !IsBusy);
        SelectRestoreCommand = new AsyncRelayCommand(SelectRestoreAsync, () => !IsBusy);
        RestoreCommand = new AsyncRelayCommand(RestoreAsync, () => !IsBusy && SelectedRestorePath is not null);
        ExportTransferCommand = new AsyncRelayCommand(ExportTransferAsync, () => !IsBusy);
        ImportTransferCommand = new AsyncRelayCommand(ImportTransferAsync, () => !IsBusy);
        _backup.StatusChanged += (_, _) => { OnPropertyChanged(nameof(MaintenanceStatus)); OnPropertyChanged(nameof(MaintenanceProgress)); };
    }

    public IReadOnlyList<ThemeMode> ThemeOptions { get; } = Enum.GetValues<ThemeMode>();
    public ThemeMode SelectedTheme { get => _selectedTheme; set { if (SetProperty(ref _selectedTheme, value) && _initialized) _ = ApplyAndSaveThemeAsync(value); } }
    public string DatabasePath { get => _databasePath; private set => SetProperty(ref _databasePath, value); }
    public string BackupDirectory { get => _backupDirectory; private set => SetProperty(ref _backupDirectory, value); }
    public string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public string? ErrorMessage { get => _errorMessage; private set => SetProperty(ref _errorMessage, value); }
    public string? LastBackupPath { get => _lastBackupPath; private set => SetProperty(ref _lastBackupPath, value); }
    public string? SelectedRestorePath { get => _selectedRestorePath; private set { if (SetProperty(ref _selectedRestorePath, value)) ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged(); } }
    public string? RestoreValidation { get => _restoreValidation; private set => SetProperty(ref _restoreValidation, value); }
    public string? LastEmergencyBackupPath { get => _lastEmergencyBackupPath; private set => SetProperty(ref _lastEmergencyBackupPath, value); }
    public string MaintenanceStatus => _backup.Status.Message;
    public int MaintenanceProgress => _backup.Status.ProgressPercent;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) { ((AsyncRelayCommand)BackupCommand).RaiseCanExecuteChanged(); ((AsyncRelayCommand)SelectRestoreCommand).RaiseCanExecuteChanged(); ((AsyncRelayCommand)RestoreCommand).RaiseCanExecuteChanged(); ((AsyncRelayCommand)ExportTransferCommand).RaiseCanExecuteChanged(); ((AsyncRelayCommand)ImportTransferCommand).RaiseCanExecuteChanged(); } } }
    public ICommand BackupCommand { get; }
    public ICommand SelectRestoreCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand ExportTransferCommand { get; }
    public ICommand ImportTransferCommand { get; }

    public async Task InitializeAsync()
    {
        var settings = await _settings.GetAsync();
        SelectedTheme = settings.ThemeMode; DatabasePath = settings.DatabasePath; BackupDirectory = settings.BackupDirectory;
        _theme.Apply(settings.ThemeMode);
        _initialized = true;
    }

    private async Task ApplyAndSaveThemeAsync(ThemeMode themeMode)
    {
        _theme.Apply(themeMode);
        ErrorMessage = null;
        try { await _settings.SaveThemeAsync(themeMode); StatusMessage = "テーマ設定を保存しました。"; }
        catch (Exception ex) { _errors.Report(ex, "Settings.Theme"); ErrorMessage = "テーマ設定を保存できませんでした。再試行してください。"; }
    }

    private async Task CreateBackupAsync()
    {
        IsBusy = true; ErrorMessage = null; StatusMessage = "バックアップを開始しています…";
        try
        {
            var result = await _backup.CreateBackupAsync();
            LastBackupPath = result.BackupPath;
            StatusMessage = $"バックアップ完了 — 整合性確認: 正常 / スキーマ v{result.SchemaVersion}";
        }
        catch (Exception ex) { _errors.Report(ex, "Settings.Backup"); ErrorMessage = "バックアップに失敗しました。現在のデータベースは変更されていません。"; StatusMessage = "バックアップ失敗"; }
        finally { IsBusy = false; OnPropertyChanged(nameof(MaintenanceStatus)); OnPropertyChanged(nameof(MaintenanceProgress)); }
    }

    private async Task SelectRestoreAsync()
    {
        var path = _restoreInteraction.SelectBackupFile();
        if (path is null) return;
        IsBusy = true; ErrorMessage = null; RestoreValidation = "選択したバックアップを検証しています…";
        try
        {
            var validation = await _restore.ValidateAsync(path);
            SelectedRestorePath = path;
            RestoreValidation = $"検証済み — 整合性確認: 正常 / スキーマ v{validation.SchemaVersion} / マイグレーション: {validation.MigrationIds.Single()}";
        }
        catch (Exception ex)
        {
            _errors.Report(ex, "Settings.RestorePreflight");
            SelectedRestorePath = null; RestoreValidation = null;
            ErrorMessage = "このバックアップは復元できません。対応するWorkout Trackerバックアップか確認してください。";
        }
        finally { IsBusy = false; }
    }

    private async Task RestoreAsync()
    {
        if (SelectedRestorePath is null || !_restoreInteraction.ConfirmRestore(SelectedRestorePath)) return;
        IsBusy = true; ErrorMessage = null; StatusMessage = "復元を開始しています…";
        try
        {
            var result = await _restore.RestoreAsync(SelectedRestorePath, true);
            LastEmergencyBackupPath = result.EmergencyBackupPath;
            StatusMessage = $"復元完了 — スキーマ v{result.RestoredDatabase.SchemaVersion}";
            await InitializeAsync();
        }
        catch (RestoreOperationException ex)
        {
            _errors.Report(ex, "Settings.Restore");
            LastEmergencyBackupPath = ex.EmergencyBackupPath;
            StatusMessage = ex.IsFatal ? "復元 / ロールバック失敗 — 通常操作停止" : "復元失敗 — 元のデータベースへ復旧済み";
            ErrorMessage = ex.IsFatal ? "復元とロールバックに失敗したため、通常操作を停止しています。緊急バックアップを保管してください。" : "復元に失敗しましたが、元のデータベースへ復旧しました。";
        }
        catch (Exception ex) { _errors.Report(ex, "Settings.RestoreStart"); StatusMessage = "復元開始前の検証に失敗"; ErrorMessage = "復元を開始できません。選択したバックアップを確認して再試行してください。"; }
        finally { IsBusy = false; OnPropertyChanged(nameof(MaintenanceStatus)); OnPropertyChanged(nameof(MaintenanceProgress)); }
    }

    private async Task ExportTransferAsync()
    {
        var path = _transferInteraction.SelectExportPath($"WorkoutTracker_{DateTime.Now:yyyy-MM-dd_HHmmss}.wtb");
        if (path is null) return;
        IsBusy = true; ErrorMessage = null; StatusMessage = "転送ファイルを作成しています…";
        try
        {
            await _transfer.ExportAsync(path, "Windows", AppVersion);
            StatusMessage = $"転送ファイルを作成しました。\n{path}";
        }
        catch (Exception ex) { _errors.Report(ex, "Transfer.Export"); StatusMessage = "エクスポート失敗"; ErrorMessage = "転送ファイルを作成できませんでした。データベースは変更されていません。"; }
        finally { IsBusy = false; }
    }

    private async Task ImportTransferAsync()
    {
        var path = _transferInteraction.SelectImportPath();
        if (path is null) return;
        IsBusy = true; ErrorMessage = null; StatusMessage = "転送ファイルを検証しています…";
        try
        {
            var plan = await _transfer.CreateImportPlanAsync(path);
            if (!_transferInteraction.ConfirmImport(plan)) { StatusMessage = "インポートをキャンセルしました。"; return; }
            var resolutions = new List<ImportResolution>();
            foreach (var conflict in plan.Conflicts)
            {
                var choice = _transferInteraction.ResolveConflict(conflict);
                if (choice is null) { StatusMessage = "インポートをキャンセルしました。"; return; }
                resolutions.Add(new(conflict.Kind, conflict.Id, choice.Value));
            }
            if (plan.Conflicts.Count > 0) StatusMessage = "競合を解決してインポートしています…";
            var result = await _transfer.ImportAsync(plan, resolutions);
            StatusMessage = $"インポート完了。追加 {result.Added} / 更新 {result.Updated} / 変更なし {result.Unchanged}";
        }
        catch (Exception ex) { _errors.Report(ex, "Transfer.Import"); StatusMessage = "インポート失敗"; ErrorMessage = "転送ファイルを適用できませんでした。データベースは変更されていません。"; }
        finally { IsBusy = false; }
    }
}
