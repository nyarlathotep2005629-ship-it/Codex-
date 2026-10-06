using System.Collections;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using WorkoutTracker.Application;
using WorkoutTracker.Domain;

namespace WorkoutTracker.App.ViewModels;

public enum WorkoutFocusField { Weight, Reps, Rpe, AddSet }
public sealed record WorkoutFocusRequest(long RequestId, Guid WorkoutExerciseId, Guid? WorkoutSetId, WorkoutFocusField Field);
public sealed record ExerciseOption(Guid ExerciseId, string ExerciseName, Guid PrimaryMuscleId, string PrimaryMuscleName);
public sealed record WorkoutFocusDestination(int? SetIndex, WorkoutFocusField Field);
public static class WorkoutFocusPlanner
{
    public static WorkoutFocusDestination Next(WorkoutFocusField current, int setIndex, int setCount) => current switch
    {
        WorkoutFocusField.Weight => new(setIndex, WorkoutFocusField.Reps),
        WorkoutFocusField.Reps => new(setIndex, WorkoutFocusField.Rpe),
        WorkoutFocusField.Rpe when setIndex + 1 < setCount => new(setIndex + 1, WorkoutFocusField.Weight),
        WorkoutFocusField.Rpe => new(null, WorkoutFocusField.AddSet),
        _ => throw new ArgumentOutOfRangeException(nameof(current))
    };
}

public sealed class WorkoutSetViewModel : ObservableObject, INotifyDataErrorInfo
{
    private readonly Action<bool, bool> _changed;
    private readonly Action<WorkoutSetViewModel, WorkoutFocusField> _moveFocus;
    private int _setNumber; private decimal? _weightKg; private int? _reps; private decimal? _rpe;
    private readonly Dictionary<string, string> _errors = [];

    public WorkoutSetViewModel(ActiveWorkoutSetDto dto, Action<bool, bool> changed, Action<WorkoutSetViewModel, WorkoutFocusField> moveFocus)
    {
        WorkoutSetId = dto.WorkoutSetId; _setNumber = dto.SetNumber; _weightKg = dto.WeightKg; _reps = dto.Reps; _rpe = dto.Rpe;
        SuggestedWeightKg = dto.SuggestedWeightKg; SuggestedReps = dto.SuggestedReps; _changed = changed; _moveFocus = moveFocus;
        AcceptWeightSuggestionCommand = new RelayCommand(() => AcceptSuggestion(WorkoutFocusField.Weight));
        AcceptRepsSuggestionCommand = new RelayCommand(() => AcceptSuggestion(WorkoutFocusField.Reps));
        MoveWeightNextCommand = new RelayCommand(() => HandleEnter(WorkoutFocusField.Weight));
        MoveRepsNextCommand = new RelayCommand(() => HandleEnter(WorkoutFocusField.Reps));
        MoveRpeNextCommand = new RelayCommand(() => HandleEnter(WorkoutFocusField.Rpe));
    }
    public Guid WorkoutSetId { get; }
    public int SetNumber { get => _setNumber; internal set => SetProperty(ref _setNumber, value); }
    public decimal? WeightKg { get => _weightKg; set { if (SetProperty(ref _weightKg, value)) { ValidateWeight(); OnPropertyChanged(nameof(HasWeightSuggestion)); _changed(false, !HasErrors); } } }
    public int? Reps { get => _reps; set { if (SetProperty(ref _reps, value)) { ValidateReps(); OnPropertyChanged(nameof(HasRepsSuggestion)); _changed(false, !HasErrors); } } }
    public decimal? Rpe { get => _rpe; set { if (SetProperty(ref _rpe, value)) { ValidateRpe(); _changed(false, !HasErrors); } } }
    public decimal? SuggestedWeightKg { get; }
    public int? SuggestedReps { get; }
    public bool HasWeightSuggestion => WeightKg is null && SuggestedWeightKg.HasValue;
    public bool HasRepsSuggestion => Reps is null && SuggestedReps.HasValue;
    public string? WeightError => GetError(nameof(WeightKg));
    public string? RepsError => GetError(nameof(Reps));
    public string? RpeError => GetError(nameof(Rpe));
    public bool HasErrors => _errors.Count != 0;
    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;
    public IEnumerable GetErrors(string? propertyName) => propertyName is not null && _errors.TryGetValue(propertyName, out var error) ? new[] { error } : Array.Empty<string>();
    public ICommand AcceptWeightSuggestionCommand { get; }
    public ICommand AcceptRepsSuggestionCommand { get; }
    public ICommand MoveWeightNextCommand { get; }
    public ICommand MoveRepsNextCommand { get; }
    public ICommand MoveRpeNextCommand { get; }

    public void HandleEnter(WorkoutFocusField field)
    {
        if (field == WorkoutFocusField.Weight && WeightKg is null && SuggestedWeightKg.HasValue) { SetProperty(ref _weightKg, SuggestedWeightKg); ValidateWeight(); OnPropertyChanged(nameof(HasWeightSuggestion)); _changed(true, true); }
        if (field == WorkoutFocusField.Reps && Reps is null && SuggestedReps.HasValue) { SetProperty(ref _reps, SuggestedReps); ValidateReps(); OnPropertyChanged(nameof(HasRepsSuggestion)); _changed(true, true); }
        _moveFocus(this, field);
    }
    private void AcceptSuggestion(WorkoutFocusField field) => HandleEnter(field);
    private void ValidateWeight() => SetError(nameof(WeightKg), WeightKg is not null && (WeightKg < .01m || WeightKg > 1000m || decimal.Truncate(WeightKg.Value * 100m) != WeightKg.Value * 100m) ? "0.01～1000.00kg、小数2桁以内で入力してください。" : null);
    private void ValidateReps() => SetError(nameof(Reps), Reps is not null && (Reps < 1 || Reps > 1000) ? "1～1000回で入力してください。" : null);
    private void ValidateRpe() => SetError(nameof(Rpe), Rpe is not null && (Rpe < 1m || Rpe > 10m || decimal.Truncate(Rpe.Value * 2m) != Rpe.Value * 2m) ? "RPEは1～10、0.5刻みで入力してください。" : null);
    private string? GetError(string property) => _errors.GetValueOrDefault(property);
    private void SetError(string property, string? error)
    {
        if (error is null) _errors.Remove(property); else _errors[property] = error;
        ErrorsChanged?.Invoke(this, new(property)); OnPropertyChanged(property + "Error"); OnPropertyChanged(nameof(HasErrors));
    }
}

public sealed class WorkoutExerciseViewModel : ObservableObject
{
    private readonly ActiveWorkoutViewModel _owner; private string? _note; private string? _noteError;
    public WorkoutExerciseViewModel(ActiveWorkoutExerciseDto dto, ActiveWorkoutViewModel owner)
    {
        _owner = owner; WorkoutExerciseId = dto.WorkoutExerciseId; ExerciseId = dto.ExerciseId; ExerciseName = dto.ExerciseName; PrimaryMuscleId = dto.PrimaryMuscleId; PrimaryMuscleName = dto.PrimaryMuscleName; DisplayOrder = dto.DisplayOrder; _note = dto.Note; PreviousPerformance = dto.PreviousPerformance;
        Sets = new(dto.Sets.Select(x => new WorkoutSetViewModel(x, (immediate, valid) => { OnSetChanged(); owner.NotifyFieldChanged(immediate, valid); }, (set, field) => owner.MoveFocus(this, set, field))));
        AddSetCommand = new RelayCommand(() => owner.AddSet(this)); RemoveSetCommand = new RelayCommand<WorkoutSetViewModel>(set => owner.RemoveSet(this, set)); RemoveExerciseCommand = new RelayCommand(() => owner.RemoveExercise(this));
    }
    public Guid WorkoutExerciseId { get; }
    public Guid ExerciseId { get; }
    public string ExerciseName { get; }
    public Guid PrimaryMuscleId { get; }
    public string PrimaryMuscleName { get; }
    public int DisplayOrder { get; internal set; }
    public PreviousPerformanceDto? PreviousPerformance { get; }
    public ObservableCollection<WorkoutSetViewModel> Sets { get; }
    public string? Note { get => _note; set { if (SetProperty(ref _note, value)) { NoteError = value?.Length > 2000 ? "種目メモは2000文字までです。" : null; _owner.NotifyFieldChanged(false, !HasErrors); } } }
    public string? NoteError { get => _noteError; private set { if (SetProperty(ref _noteError, value)) OnPropertyChanged(nameof(HasErrors)); } }
    public bool HasErrors => NoteError is not null;
    public decimal CurrentVolume => Sets.Where(x => x.WeightKg.HasValue && x.Reps.HasValue).Sum(x => x.WeightKg!.Value * x.Reps!.Value);
    public decimal? EstimatedOneRepMax => Sets.Where(x => x.WeightKg.HasValue && x.Reps is >= 1 and <= 15).Select(x => WorkoutCalculations.EpleyOneRepMax(x.WeightKg!.Value, x.Reps!.Value)).Where(x => x.HasValue).Select(x => x!.Value).DefaultIfEmpty().Max() is var value && value > 0 ? value : null;
    public ICommand AddSetCommand { get; }
    public ICommand RemoveSetCommand { get; }
    public ICommand RemoveExerciseCommand { get; }
    internal void OnSetChanged() { OnPropertyChanged(nameof(CurrentVolume)); OnPropertyChanged(nameof(EstimatedOneRepMax)); }
}

public sealed class ActiveWorkoutViewModel : ObservableObject
{
    private readonly LoadActiveWorkoutUseCase _load; private readonly StartWorkoutUseCase _start; private readonly GetPreviousPerformanceUseCase _previous;
    private readonly FinishWorkoutUseCase _finish; private readonly DiscardWorkoutUseCase _discard; private readonly AutoSaveCoordinator _autoSave; private readonly ActiveWorkoutState _state;
    private readonly IExerciseRepository _exercises; private readonly IWorkoutMenuRepository _menus;
    private readonly IOffUiExecutor _executor;
    private long _focusRequestId; private WorkoutFocusRequest? _currentFocusRequest; private string? _lastOperationError; private ExerciseOption? _selectedExercise;
    private DateOnly _workoutDate; private string? _workoutName; private string? _workoutNote;
    private readonly HashSet<string> _metadataErrors = [];

    public ActiveWorkoutViewModel(LoadActiveWorkoutUseCase load, StartWorkoutUseCase start, GetPreviousPerformanceUseCase previous, FinishWorkoutUseCase finish, DiscardWorkoutUseCase discard, AutoSaveCoordinator autoSave, ActiveWorkoutState state, IExerciseRepository exercises, IWorkoutMenuRepository menus, IOffUiExecutor executor)
    {
        _load = load; _start = start; _previous = previous; _finish = finish; _discard = discard; _autoSave = autoSave; _state = state; _exercises = exercises; _menus = menus; _executor = executor;
        Exercises = []; AvailableExercises = []; AvailableMenus = [];
        FinishWorkoutCommand = new AsyncRelayCommand(FinishAsync); RetrySaveCommand = new AsyncRelayCommand(() => _autoSave.RetryAsync());
        AddExerciseCommand = new AsyncRelayCommand(AddSelectedExerciseAsync, () => SelectedExercise is not null);
        _state.Changed += (_, _) => RaiseStateProperties();
    }
    public event EventHandler<WorkoutFocusRequest>? FocusRequested;
    public ObservableCollection<WorkoutExerciseViewModel> Exercises { get; }
    public ObservableCollection<ExerciseOption> AvailableExercises { get; }
    public ObservableCollection<WorkoutMenuSummary> AvailableMenus { get; }
    public Guid? SessionId => _state.SessionId;
    public bool IsActive => _state.IsActive;
    public DateOnly WorkoutDate { get => _workoutDate; set { if (SetProperty(ref _workoutDate, value)) ValidateMetadata(nameof(WorkoutDate), value < new DateOnly(1900, 1, 1) || value > DateOnly.FromDateTime(DateTime.Today), "ワークアウト日は1900-01-01から今日までです。", true); } }
    public string? WorkoutName { get => _workoutName; set { if (SetProperty(ref _workoutName, value)) ValidateMetadata(nameof(WorkoutName), value?.Trim().Length > 100, "ワークアウト名は100文字までです。", false); } }
    public string? WorkoutNote { get => _workoutNote; set { if (SetProperty(ref _workoutNote, value)) ValidateMetadata(nameof(WorkoutNote), value?.Length > 4000, "ワークアウトメモは4000文字までです。", false); } }
    public WorkoutSaveState SaveState => _state.SaveState;
    public long EditRevision => _state.EditRevision;
    public long LastSavedRevision => _state.LastSavedRevision;
    public bool HasUnsavedChanges => _state.HasUnsavedChanges;
    public int TotalSets => Exercises.Sum(x => x.Sets.Count);
    public decimal TotalVolume => Exercises.Sum(x => x.CurrentVolume);
    public string SaveStateText => SaveState switch { WorkoutSaveState.Saving => "保存中...", WorkoutSaveState.Failed => "保存できませんでした", WorkoutSaveState.Dirty => "未保存", _ => "保存済み" };
    public string? LastOperationError { get => _lastOperationError; private set => SetProperty(ref _lastOperationError, value); }
    public ExerciseOption? SelectedExercise { get => _selectedExercise; set { if (SetProperty(ref _selectedExercise, value)) ((AsyncRelayCommand)AddExerciseCommand).RaiseCanExecuteChanged(); } }
    public ICommand FinishWorkoutCommand { get; }
    public ICommand RetrySaveCommand { get; }
    public ICommand AddExerciseCommand { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var exerciseRows = await _executor.RunAsync(ct => _exercises.GetActiveAsync(ct), cancellationToken); AvailableExercises.Clear(); foreach (var row in exerciseRows) AvailableExercises.Add(new(row.Id, row.Name, row.PrimaryMuscleId, row.PrimaryMuscleName));
        var menuRows = await _executor.RunAsync(ct => _menus.GetSummariesAsync(ct), cancellationToken); AvailableMenus.Clear(); foreach (var row in menuRows) AvailableMenus.Add(row);
        var workout = await _load.ExecuteAsync(cancellationToken); if (workout is not null) Load(workout); else { _autoSave.Resume(); RaiseStateProperties(); }
    }
    public async Task ReloadFromDatabaseAsync(CancellationToken cancellationToken = default)
    {
        _autoSave.BeginWorkout();
        _state.Clear();
        Exercises.Clear(); AvailableExercises.Clear(); AvailableMenus.Clear();
        await InitializeAsync(cancellationToken);
    }
    public async Task<StartWorkoutResult> StartAsync(Guid? menuId, DateOnly date, CancellationToken cancellationToken = default)
    {
        var result = await _start.ExecuteAsync(new(menuId, date), cancellationToken);
        if (result is StartWorkoutResult.Started started) Load(started.Workout);
        else await InitializeAsync(cancellationToken);
        return result;
    }
    public async Task<bool> DiscardAsync(bool confirmed, CancellationToken cancellationToken = default)
    {
        try { var discarded = await _discard.ExecuteAsync(confirmed, cancellationToken); if (discarded) { Exercises.Clear(); RaiseStateProperties(); } return discarded; }
        catch { ReportOperationError("ワークアウトを破棄できませんでした。再試行してください。"); return false; }
    }
    public void ReportOperationError(string message) => LastOperationError = message;
    public void AddSet(WorkoutExerciseViewModel exercise)
    {
        if (exercise.Sets.Count >= 100) { LastOperationError = "セットは100件までです。"; return; }
        var set = new WorkoutSetViewModel(new(Guid.NewGuid(), exercise.Sets.Count + 1, null, null, null, null, null), (immediate, valid) => { exercise.OnSetChanged(); NotifyFieldChanged(immediate, valid); }, (row, field) => MoveFocus(exercise, row, field));
        exercise.Sets.Add(set); exercise.OnSetChanged(); NotifyEdited(true); RequestFocus(exercise, set, WorkoutFocusField.Weight);
    }
    public void RemoveSet(WorkoutExerciseViewModel exercise, WorkoutSetViewModel set)
    {
        if (exercise.Sets.Count <= 1) { LastOperationError = "セットは1件以上必要です。"; return; }
        if (_currentFocusRequest?.WorkoutSetId == set.WorkoutSetId) InvalidateFocusRequest();
        exercise.Sets.Remove(set); RenumberSets(exercise); exercise.OnSetChanged(); NotifyEdited(true);
    }
    public void RemoveExercise(WorkoutExerciseViewModel exercise)
    {
        if (_currentFocusRequest?.WorkoutExerciseId == exercise.WorkoutExerciseId) InvalidateFocusRequest();
        Exercises.Remove(exercise); RenumberExercises(); NotifyEdited(true);
    }
    public void NotifyFieldChanged(bool immediate, bool canSave)
    {
        OnPropertyChanged(nameof(Exercises)); OnPropertyChanged(nameof(TotalSets)); OnPropertyChanged(nameof(TotalVolume));
        if (canSave) NotifyEdited(immediate);
        else { _state.ApplyEdit(BuildDto()); RaiseStateProperties(); }
    }
    public void MoveFocus(WorkoutExerciseViewModel exercise, WorkoutSetViewModel set, WorkoutFocusField field)
    {
        var index = exercise.Sets.IndexOf(set);
        var destination = WorkoutFocusPlanner.Next(field, index, exercise.Sets.Count);
        RequestFocus(exercise, destination.SetIndex.HasValue ? exercise.Sets[destination.SetIndex.Value] : null, destination.Field);
    }
    public bool IsCurrentFocusRequest(long requestId) => _currentFocusRequest?.RequestId == requestId;
    public void InvalidateFocusRequest() { _currentFocusRequest = null; }

    private async Task AddSelectedExerciseAsync()
    {
        var option = SelectedExercise; if (option is null) return;
        if (Exercises.Count >= 50) { LastOperationError = "種目は50件までです。"; return; }
        if (Exercises.Any(x => x.ExerciseId == option.ExerciseId)) { LastOperationError = "同じ種目は追加できません。"; return; }
        PreviousPerformanceDto? previous;
        try { previous = await _previous.ExecuteAsync(option.ExerciseId); }
        catch { ReportOperationError("前回の記録を取得できませんでした。"); return; }
        var suggestion = previous?.Sets.SingleOrDefault(x => x.SetNumber == 1);
        var dto = new ActiveWorkoutExerciseDto(Guid.NewGuid(), option.ExerciseId, option.ExerciseName, option.PrimaryMuscleId, option.PrimaryMuscleName, Exercises.Count, null, previous,
            [new ActiveWorkoutSetDto(Guid.NewGuid(), 1, null, null, null, suggestion?.WeightKg, suggestion?.Reps)]);
        var vm = new WorkoutExerciseViewModel(dto, this); Exercises.Add(vm); NotifyEdited(true); RequestFocus(vm, vm.Sets[0], WorkoutFocusField.Weight);
    }
    public async Task FinishAsync()
    {
        if (_metadataErrors.Count != 0 || Exercises.Any(x => x.HasErrors) || Exercises.SelectMany(x => x.Sets).Any(x => x.HasErrors)) { LastOperationError = "入力エラーを修正してください。"; return; }
        try
        {
            var result = await _finish.ExecuteAsync();
            if (!result.Succeeded) LastOperationError = string.Join(Environment.NewLine, result.Errors); else { Exercises.Clear(); RaiseStateProperties(); }
        }
        catch { ReportOperationError("ワークアウトを完了できませんでした。入力は下書きに保持されています。"); }
    }
    private void Load(ActiveWorkoutDto workout)
    {
        _autoSave.BeginWorkout();
        _metadataErrors.Clear();
        _workoutDate = workout.WorkoutDate; _workoutName = workout.WorkoutName; _workoutNote = workout.WorkoutNote; OnPropertyChanged(nameof(WorkoutDate)); OnPropertyChanged(nameof(WorkoutName)); OnPropertyChanged(nameof(WorkoutNote)); Exercises.Clear();
        foreach (var exercise in workout.Exercises.OrderBy(x => x.DisplayOrder)) Exercises.Add(new WorkoutExerciseViewModel(exercise, this));
        RaiseStateProperties();
    }
    private void NotifyEdited(bool immediate)
    {
        OnPropertyChanged(nameof(TotalSets)); OnPropertyChanged(nameof(TotalVolume));
        var dto = BuildDto();
        if (!CanPersistCurrent()) { _state.ApplyEdit(dto); RaiseStateProperties(); return; }
        LastOperationError = null; var payload = BuildSaveRequest(dto); _autoSave.NotifyChanged(dto, payload, immediate); RaiseStateProperties();
    }
    private bool CanPersistCurrent() => _metadataErrors.Count == 0 && Exercises.All(x => !x.HasErrors) && Exercises.SelectMany(x => x.Sets).All(x => !x.HasErrors);
    private void ValidateMetadata(string property, bool invalid, string message, bool immediate)
    {
        if (invalid)
        {
            _metadataErrors.Add(property); LastOperationError = message; _state.ApplyEdit(BuildDto()); RaiseStateProperties();
        }
        else
        {
            _metadataErrors.Remove(property); NotifyEdited(immediate);
        }
    }
    private ActiveWorkoutDto BuildDto() => new(SessionId ?? throw new InvalidOperationException("Active Draft is not loaded."), _state.CurrentWorkout?.SourceMenuId, WorkoutName, WorkoutDate, WorkoutNote,
        Exercises.Select(x => new ActiveWorkoutExerciseDto(x.WorkoutExerciseId, x.ExerciseId, x.ExerciseName, x.PrimaryMuscleId, x.PrimaryMuscleName, x.DisplayOrder, x.Note, x.PreviousPerformance,
            x.Sets.Select(s => new ActiveWorkoutSetDto(s.WorkoutSetId, s.SetNumber, s.WeightKg, s.Reps, s.Rpe, s.SuggestedWeightKg, s.SuggestedReps)).ToArray())).ToArray());
    private SaveDraftRequest BuildSaveRequest(ActiveWorkoutDto dto) => new(dto.SessionId, dto.WorkoutDate, dto.WorkoutName, WorkoutNote,
        dto.Exercises.Select(x => new SaveDraftExerciseRequest(x.WorkoutExerciseId, x.ExerciseId, x.ExerciseName, x.PrimaryMuscleId, x.PrimaryMuscleName, x.DisplayOrder, x.Note,
            x.Sets.Select(s => new SaveDraftSetRequest(s.WorkoutSetId, s.SetNumber, s.WeightKg, s.Reps, s.Rpe)).ToArray())).ToArray());
    private void RequestFocus(WorkoutExerciseViewModel exercise, WorkoutSetViewModel? set, WorkoutFocusField field)
    {
        var request = new WorkoutFocusRequest(Interlocked.Increment(ref _focusRequestId), exercise.WorkoutExerciseId, set?.WorkoutSetId, field); _currentFocusRequest = request; FocusRequested?.Invoke(this, request);
    }
    private static void RenumberSets(WorkoutExerciseViewModel exercise) { for (var i = 0; i < exercise.Sets.Count; i++) exercise.Sets[i].SetNumber = i + 1; }
    private void RenumberExercises() { for (var i = 0; i < Exercises.Count; i++) Exercises[i].DisplayOrder = i; }
    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(SessionId)); OnPropertyChanged(nameof(IsActive)); OnPropertyChanged(nameof(SaveState)); OnPropertyChanged(nameof(SaveStateText)); OnPropertyChanged(nameof(EditRevision)); OnPropertyChanged(nameof(LastSavedRevision)); OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(TotalSets)); OnPropertyChanged(nameof(TotalVolume));
    }
}
