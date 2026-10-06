using WorkoutTracker.Application;
using WorkoutTracker.Domain;
using WorkoutTracker.Infrastructure.Persistence;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;

namespace WorkoutTracker.Mobile;

/// <summary>
/// Android presentation coordinator. It never opens a DbContext: all persistence goes through
/// existing Application contracts and the shared AutoSaveCoordinator.
/// </summary>
public sealed class MobileWorkoutController
{
    private readonly IDatabaseInitializer _databaseInitializer;
    private readonly LoadActiveWorkoutUseCase _load;
    private readonly StartWorkoutUseCase _start;
    private readonly GetPreviousPerformanceUseCase _previous;
    private readonly FinishWorkoutUseCase _finish;
    private readonly DiscardWorkoutUseCase _discard;
    private readonly AutoSaveCoordinator _autoSave;
    private readonly ActiveWorkoutState _state;
    private readonly IWorkoutMenuRepository _menus;
    private readonly HistoryCalendarService _history;
    private readonly MasterDataService _masterData;
    private readonly AnalyticsService _analytics;
    private readonly RestTimerState _restTimer;
    private readonly ITransferService _transfer;
    private readonly ITransferFileSaveService _transferFileSave;
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private bool _started;

    public MobileWorkoutController(
        IDatabaseInitializer databaseInitializer,
        LoadActiveWorkoutUseCase load,
        StartWorkoutUseCase start,
        GetPreviousPerformanceUseCase previous,
        FinishWorkoutUseCase finish,
        DiscardWorkoutUseCase discard,
        AutoSaveCoordinator autoSave,
        ActiveWorkoutState state,
        IWorkoutMenuRepository menus,
        HistoryCalendarService history,
        MasterDataService masterData,
        AnalyticsService analytics,
        RestTimerState restTimer,
        ITransferService transfer,
        ITransferFileSaveService transferFileSave)
    {
        _databaseInitializer = databaseInitializer;
        _load = load;
        _start = start;
        _previous = previous;
        _finish = finish;
        _discard = discard;
        _autoSave = autoSave;
        _state = state;
        _menus = menus;
        _history = history;
        _masterData = masterData;
        _analytics = analytics;
        _restTimer = restTimer;
        _transfer = transfer;
        _transferFileSave = transferFileSave;
        _state.Changed += (_, _) => RaiseChanged();
        _restTimer.Changed += (_, _) => RaiseChanged();
    }

    public event EventHandler? Changed;
    public IReadOnlyList<WorkoutMenuSummary> Menus { get; private set; } = [];
    public IReadOnlyList<WorkoutHistoryItem> RecentWorkouts { get; private set; } = [];
    public ActiveWorkoutDto? CurrentWorkout => _state.CurrentWorkout;
    public bool HasActiveWorkout => _state.IsActive;
    public string SaveStateText => _state.SaveState switch
    {
        WorkoutSaveState.Saving => "保存中...",
        WorkoutSaveState.Failed => "保存エラー（入力は保持されています）",
        WorkoutSaveState.Dirty => "保存待ち...",
        _ => "保存済み"
    };
    public string? LastError { get; private set; }
    public TimeSpan RestTimerRemaining => _restTimer.GetRemaining();
    public bool IsRestTimerRunning => _restTimer.IsRunning;

    public async Task<TransferFileSaveResult> SaveTransferToFileAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var temporary = await ExportTransferToCacheAsync(cancellationToken);
        try
        {
            await using var input = File.OpenRead(temporary.Result.Path);
            return await _transferFileSave.SaveAsync(Path.GetFileName(temporary.Result.Path), input, cancellationToken);
        }
        finally { TryDelete(temporary.Result.Path); }
    }

    public async Task RequestTransferShareAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var temporary = await ExportTransferToCacheAsync(cancellationToken);
        try { await Share.Default.RequestAsync(new ShareFileRequest("Workout Tracker転送ファイル", new ShareFile(temporary.Result.Path))); }
        finally { TryDelete(temporary.Result.Path); }
    }

    private async Task<(TransferFileResult Result, string Path)> ExportTransferToCacheAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(FileSystem.CacheDirectory, $"WorkoutTracker_{DateTime.Now:yyyy-MM-dd_HHmmss}.wtb");
        return (await _transfer.ExportAsync(path, "Android", AppInfo.Current.VersionString, cancellationToken), path);
    }

    private static void TryDelete(string path) { try { File.Delete(path); } catch { } }

    public async Task<ImportPlan?> PickImportPlanAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var file = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Workout Trackerデータを選択", FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>> { [DevicePlatform.Android] = ["application/zip", "application/octet-stream", "*/*"] }) });
        if (file is null) return null;
        var temporaryPath = Path.Combine(FileSystem.CacheDirectory, $"incoming-{Guid.NewGuid():N}.wtb");
        await using (var input = await file.OpenReadAsync())
        await using (var output = File.Create(temporaryPath)) await input.CopyToAsync(output, cancellationToken);
        try { return await _transfer.CreateImportPlanAsync(temporaryPath, cancellationToken); }
        finally { try { File.Delete(temporaryPath); } catch { } }
    }

    public Task<ImportResult> ImportTransferAsync(ImportPlan plan, IReadOnlyList<ImportResolution> resolutions, CancellationToken cancellationToken = default) => _transfer.ImportAsync(plan, resolutions, cancellationToken);

    public async Task EnsureStartedAsync(CancellationToken cancellationToken = default)
    {
        if (_started) return;
        await _startupGate.WaitAsync(cancellationToken);
        try
        {
            if (_started) return;
            await _databaseInitializer.InitializeFreshDatabaseAsync(cancellationToken);
            await RefreshCatalogAsync(cancellationToken);
            await _load.ExecuteAsync(cancellationToken);
            _autoSave.Resume();
            _started = true;
            RaiseChanged();
        }
        finally { _startupGate.Release(); }
    }

    public async Task RefreshHomeAsync(CancellationToken cancellationToken = default)
    {
        var page = await GetHistoryPageAsync(new HistoryQuery(null, null, null, null, null, 25, null), cancellationToken);
        RecentWorkouts = page.Items.Take(3).ToArray();
        RaiseChanged();
    }

    /// <summary>Reads completed-workout history through the existing Application query contract.</summary>
    public async Task<WorkoutTracker.Application.HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _history.GetHistoryPageAsync(query, cancellationToken);
    }

    /// <summary>Gets the completed-history filter candidates; no mobile persistence access is involved.</summary>
    public async Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _history.GetHistoryFilterOptionsAsync(cancellationToken);
    }

    /// <summary>Gets the immutable completion snapshot used by the detail screen.</summary>
    public async Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _history.GetWorkoutDetailAsync(sessionId, cancellationToken);
    }

    /// <summary>Gets all days for one month through the existing calendar query contract.</summary>
    public async Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _history.GetCalendarMonthAsync(year, month, cancellationToken);
    }

    /// <summary>Mobile dashboard and statistics only consume the shared analytics contract.</summary>
    public async Task<DashboardDto> GetDashboardAsync(DashboardPeriod period, DateOnly displayDate, IReadOnlyList<Guid> exerciseIds, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _analytics.GetDashboardAsync(period, displayDate, exerciseIds, cancellationToken);
    }

    public async Task<IReadOnlyList<ExerciseOptionDto>> GetAnalyticsExerciseOptionsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _analytics.GetExerciseOptionsAsync(cancellationToken);
    }

    public async Task<StatisticsDto> GetStatisticsAsync(Guid exerciseId, DashboardPeriod period, StatisticsMetric metric, DateOnly displayDate, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _analytics.GetStatisticsAsync(exerciseId, period, metric, displayDate, cancellationToken);
    }

    public void StartRestTimer(TimeSpan duration) => _restTimer.Start(duration);
    public void AddRestTimer(TimeSpan duration) => _restTimer.Add(duration);
    public void StopRestTimer() => _restTimer.Stop();

    // Master-data operations remain in MasterDataService so Mobile has no duplicate rules or persistence code.
    public async Task<IReadOnlyList<MuscleGroupDto>> GetMuscleGroupsAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _masterData.GetMuscleGroupsAsync(cancellationToken);
    }

    public async Task<MuscleGroupDto> SaveMuscleGroupAsync(SaveMuscleGroupRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _masterData.SaveMuscleGroupAsync(request, cancellationToken);
    }

    public async Task<MuscleGroupDeleteResult> DeleteMuscleGroupAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _masterData.DeleteMuscleGroupAsync(id, cancellationToken);
    }

    public async Task ReorderMuscleGroupsAsync(IReadOnlyList<Guid> ids, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        await _masterData.ReorderMuscleGroupsAsync(ids, cancellationToken);
    }

    public async Task<IReadOnlyList<ExerciseMasterDto>> GetExercisesAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _masterData.GetExercisesAsync(includeInactive, cancellationToken);
    }

    public async Task<ExerciseMasterDto> SaveExerciseAsync(SaveExerciseRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _masterData.SaveExerciseAsync(request, cancellationToken);
    }

    public async Task SetExerciseActiveAsync(Guid id, bool active, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        await _masterData.SetExerciseActiveAsync(id, active, cancellationToken);
    }

    public async Task<ExerciseDeleteResult> DeleteExerciseAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _masterData.DeleteExerciseAsync(id, cancellationToken);
    }

    public async Task<IReadOnlyList<WorkoutMenuMasterDto>> GetMasterMenusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        return await _masterData.GetMenusAsync(cancellationToken);
    }

    public async Task<WorkoutMenuMasterDto> SaveMenuAsync(SaveWorkoutMenuRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var saved = await _masterData.SaveMenuAsync(request, cancellationToken);
        await RefreshCatalogAsync(cancellationToken);
        return saved;
    }

    public async Task DeleteMenuAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        await _masterData.DeleteMenuAsync(id, cancellationToken);
        await RefreshCatalogAsync(cancellationToken);
    }

    public async Task RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        Menus = await _menus.GetSummariesAsync(cancellationToken);
        RaiseChanged();
    }

    public async Task<StartWorkoutResult> StartAsync(Guid? menuId, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        LastError = null;
        var result = await _start.ExecuteAsync(new StartWorkoutRequest(menuId, DateOnly.FromDateTime(DateTime.Today)), cancellationToken);
        if (result is StartWorkoutResult.ExistingDraft) await _load.ExecuteAsync(cancellationToken);
        _autoSave.BeginWorkout();
        RaiseChanged();
        return result;
    }

    public async Task ResumeAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        await _load.ExecuteAsync(cancellationToken);
        _autoSave.BeginWorkout();
        RaiseChanged();
    }

    public bool UpdateWeight(Guid workoutSetId, string value)
    {
        if (!TryParseDecimal(value, "重量", 0.01m, 1000m, out var result)) return false;
        return UpdateSet(workoutSetId, result, null, null, SetField.Weight);
    }

    public bool UpdateReps(Guid workoutSetId, string value)
    {
        if (!TryParseInt(value, "回数", 1, 1000, out var result)) return false;
        return UpdateSet(workoutSetId, null, result, null, SetField.Reps);
    }

    public bool UpdateRpe(Guid workoutSetId, string value)
    {
        if (!TryParseDecimal(value, "RPE", 1m, 10m, out var result)) return false;
        if (result is not null && decimal.Round(result.Value * 2m) != result.Value * 2m) { SetError("RPEは0.5刻みで入力してください。"); return false; }
        return UpdateSet(workoutSetId, null, null, result, SetField.Rpe);
    }

    public void AddSet(Guid workoutExerciseId)
    {
        var current = CurrentWorkout ?? throw new InvalidOperationException("Active Draft is not loaded.");
        var exercise = current.Exercises.Single(x => x.WorkoutExerciseId == workoutExerciseId);
        if (exercise.Sets.Count >= 100) { SetError("セットは100件までです。"); return; }
        var newSet = new ActiveWorkoutSetDto(Guid.NewGuid(), exercise.Sets.Count + 1, null, null, null, null, null);
        var updatedExercise = exercise with { Sets = [.. exercise.Sets, newSet] };
        var updated = current with { Exercises = current.Exercises.Select(x => x.WorkoutExerciseId == workoutExerciseId ? updatedExercise : x).ToArray() };
        QueueSave(updated);
    }

    /// <summary>Returns only active master rows that are not already in the active Draft.</summary>
    public async Task<IReadOnlyList<ExerciseMasterDto>> GetAddableExercisesAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var existing = CurrentWorkout?.Exercises.Select(x => x.ExerciseId).ToHashSet() ?? [];
        return (await _masterData.GetExercisesAsync(includeInactive: false, cancellationToken))
            .Where(x => !existing.Contains(x.Id))
            .ToArray();
    }

    public async Task<bool> AddExerciseToActiveWorkoutAsync(Guid exerciseId, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var current = CurrentWorkout;
        if (current is null) { SetError("Active Draftがありません。"); return false; }
        if (current.Exercises.Any(x => x.ExerciseId == exerciseId)) { SetError("同じ種目は追加できません。"); return false; }

        var exercise = (await _masterData.GetExercisesAsync(includeInactive: false, cancellationToken)).SingleOrDefault(x => x.Id == exerciseId);
        if (exercise is null) { SetError("追加対象の有効な種目が見つかりません。"); return false; }

        try
        {
            var previous = await _previous.ExecuteAsync(exerciseId, cancellationToken);
            var updated = ActiveWorkoutDraftComposer.AddMissing(current, [new(exercise, 1, null, previous)]);
            QueueSave(updated);
            return true;
        }
        catch (DomainValidationException ex) { SetError(ex.Message); return false; }
        catch { SetError("種目を追加できませんでした。"); return false; }
    }

    /// <summary>
    /// Merges only missing, currently active menu exercises into the existing Draft. Existing
    /// exercise rows and entered sets are deliberately left untouched.
    /// </summary>
    public async Task<int> AddMenuToActiveWorkoutAsync(Guid menuId, CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        var current = CurrentWorkout;
        if (current is null) { SetError("Active Draftがありません。"); return 0; }

        var menu = (await _masterData.GetMenusAsync(cancellationToken)).SingleOrDefault(x => x.Id == menuId);
        if (menu is null) { SetError("指定されたメニューが見つかりません。"); return 0; }
        var activeById = (await _masterData.GetExercisesAsync(includeInactive: false, cancellationToken)).ToDictionary(x => x.Id);
        var existing = current.Exercises.Select(x => x.ExerciseId).ToHashSet();
        var candidates = menu.Exercises.OrderBy(x => x.DisplayOrder)
            .Where(x => !existing.Contains(x.ExerciseId) && activeById.ContainsKey(x.ExerciseId))
            .ToArray();
        if (candidates.Length == 0) { SetError("このメニューに追加できる有効な種目はありません。"); return 0; }
        if (current.Exercises.Count + candidates.Length > 50) { SetError("WorkoutのExerciseは50件までです。"); return 0; }

        try
        {
            var additions = new List<ActiveWorkoutExerciseAddition>(candidates.Length);
            foreach (var item in candidates)
            {
                var previous = await _previous.ExecuteAsync(item.ExerciseId, cancellationToken);
                additions.Add(new(activeById[item.ExerciseId], item.DefaultSetCount, item.Note, previous));
            }
            var updated = ActiveWorkoutDraftComposer.AddMissing(current, additions);
            QueueSave(updated);
            return updated.Exercises.Count - current.Exercises.Count;
        }
        catch (DomainValidationException ex) { SetError(ex.Message); return 0; }
        catch { SetError("メニューの種目を追加できませんでした。"); return 0; }
    }

    public async Task<bool> CancelActiveWorkoutAsync(CancellationToken cancellationToken = default)
    {
        await EnsureStartedAsync(cancellationToken);
        try
        {
            var discarded = await _discard.ExecuteAsync(confirmed: true, cancellationToken);
            if (discarded)
            {
                _restTimer.Stop();
                LastError = null;
                RaiseChanged();
            }
            return discarded;
        }
        catch { SetError("ワークアウトを中止できませんでした。再試行してください。"); return false; }
    }

    public async Task<bool> FinishAsync(CancellationToken cancellationToken = default)
    {
        if (!HasActiveWorkout) return false;
        LastError = null;
        try
        {
            var result = await _finish.ExecuteAsync(cancellationToken);
            if (!result.Succeeded) { SetError(string.Join(Environment.NewLine, result.Errors)); return false; }
            await RefreshHomeAsync(cancellationToken);
            return true;
        }
        catch
        {
            SetError("完了を保存できませんでした。入力は下書きに保持されています。");
            return false;
        }
    }

    /// <summary>Best effort only. SQLite's committed Draft remains the source of truth.</summary>
    public async Task FlushForBackgroundAsync()
    {
        if (!HasActiveWorkout) return;
        try { await _autoSave.FlushAsync(); }
        catch { SetError("保存エラー（入力は保持されています）"); }
    }

    private bool UpdateSet(Guid workoutSetId, decimal? decimalValue, int? intValue, decimal? rpeValue, SetField field)
    {
        var current = CurrentWorkout;
        if (current is null) return false;
        LastError = null;
        var changed = false;
        var exercises = current.Exercises.Select(exercise => exercise with
        {
            Sets = exercise.Sets.Select(set =>
            {
                if (set.WorkoutSetId != workoutSetId) return set;
                changed = true;
                return field switch
                {
                    SetField.Weight => set with { WeightKg = decimalValue },
                    SetField.Reps => set with { Reps = intValue },
                    SetField.Rpe => set with { Rpe = rpeValue },
                    _ => set
                };
            }).ToArray()
        }).ToArray();
        if (!changed) return false;
        QueueSave(current with { Exercises = exercises });
        return true;
    }

    private void QueueSave(ActiveWorkoutDto updated)
    {
        try
        {
            _autoSave.NotifyChanged(updated, BuildSaveRequest(updated), immediate: false);
            RaiseChanged();
        }
        catch { SetError("保存キューを更新できませんでした。入力は画面に保持されています。"); }
    }

    private static SaveDraftRequest BuildSaveRequest(ActiveWorkoutDto workout) => new(
        workout.SessionId, workout.WorkoutDate, workout.WorkoutName, workout.WorkoutNote,
        workout.Exercises.OrderBy(x => x.DisplayOrder).Select(exercise => new SaveDraftExerciseRequest(
            exercise.WorkoutExerciseId, exercise.ExerciseId, exercise.ExerciseName, exercise.PrimaryMuscleId,
            exercise.PrimaryMuscleName, exercise.DisplayOrder, exercise.Note,
            exercise.Sets.OrderBy(set => set.SetNumber).Select(set => new SaveDraftSetRequest(
                set.WorkoutSetId, set.SetNumber, set.WeightKg, set.Reps, set.Rpe)).ToArray())).ToArray());

    private bool TryParseDecimal(string value, string label, decimal minimum, decimal maximum, out decimal? result)
    {
        if (string.IsNullOrWhiteSpace(value)) { result = null; return true; }
        if (decimal.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum) { result = parsed; return true; }
        result = null; SetError($"{label}は有効な数値で入力してください。"); return false;
    }

    private bool TryParseInt(string value, string label, int minimum, int maximum, out int? result)
    {
        if (string.IsNullOrWhiteSpace(value)) { result = null; return true; }
        if (int.TryParse(value, out var parsed) && parsed >= minimum && parsed <= maximum) { result = parsed; return true; }
        result = null; SetError($"{label}は{minimum}から{maximum}で入力してください。"); return false;
    }

    private void SetError(string message) { LastError = message; RaiseChanged(); }
    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    private enum SetField { Weight, Reps, Rpe }
}
