using System.Collections.ObjectModel;
using System.Windows.Input;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;
using WorkoutTracker.Domain;

namespace WorkoutTracker.App.ViewModels;

public sealed class MuscleGroupItemViewModel(MuscleGroupDto dto) : ObservableObject
{
    public Guid Id => dto.Id;
    public string Name => dto.Name;
    public int DisplayOrder => dto.DisplayOrder;
}

public sealed class MuscleChoiceViewModel(MuscleGroupItemViewModel muscle) : ObservableObject
{
    private bool _isSelected;
    public MuscleGroupItemViewModel Muscle { get; } = muscle;
    public string Name => Muscle.Name;
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class ExerciseMasterItemViewModel(ExerciseMasterDto dto) : ObservableObject
{
    public ExerciseMasterDto Dto { get; } = dto;
    public Guid Id => Dto.Id;
    public string Name => Dto.Name;
    public string PrimaryMuscleName => Dto.PrimaryMuscleName;
    public string SecondaryMuscles => string.Join("、", Dto.SecondaryMuscleNames);
    public bool IsActive => Dto.IsActive;
    public bool IsReferenced => Dto.IsReferenced;
    public string StateLabel => IsActive ? "使用中" : "非表示";
}

public sealed class MenuMasterItemViewModel(WorkoutMenuMasterDto dto) : ObservableObject
{
    public WorkoutMenuMasterDto Dto { get; } = dto;
    public Guid Id => Dto.Id;
    public string Name => Dto.Name;
    public string ExerciseSummary => string.Join(" / ", Dto.Exercises.Select(x => x.ExerciseName));
    public int ExerciseCount => Dto.Exercises.Count;
    public int PlannedSetCount => Dto.PlannedSetCount;
}

public sealed class MenuExerciseEditorViewModel(Guid? id, ExerciseMasterItemViewModel exercise, int defaultSetCount, string? note) : ObservableObject
{
    private int _defaultSetCount = defaultSetCount;
    private string? _note = note;
    public Guid? Id { get; } = id;
    public Guid ExerciseId => Exercise.Id;
    public ExerciseMasterItemViewModel Exercise { get; } = exercise;
    public string ExerciseName => Exercise.Name;
    public int DefaultSetCount { get => _defaultSetCount; set { if (SetProperty(ref _defaultSetCount, value)) OnPropertyChanged(nameof(DefaultSetCountError)); } }
    public string? DefaultSetCountError => DefaultSetCount is < 1 or > 20 ? "予定セット数は1〜20で入力してください。" : null;
    public string? Note { get => _note; set => SetProperty(ref _note, value); }
}

public sealed class MasterDataViewModel : ObservableObject
{
    private readonly MasterDataService _service;
    private readonly ActiveWorkoutViewModel _workout;
    private readonly IUiExceptionReporter _errors;
    private MuscleGroupItemViewModel? _selectedMuscleGroup;
    private ExerciseMasterItemViewModel? _selectedExercise;
    private MenuMasterItemViewModel? _selectedMenu;
    private ExerciseMasterItemViewModel? _exerciseToAdd;
    private MuscleGroupItemViewModel? _exercisePrimaryMuscle;
    private string _muscleName = string.Empty;
    private string _exerciseName = string.Empty;
    private string? _exerciseNote;
    private bool _exerciseIsActive = true;
    private string _menuName = string.Empty;
    private string? _menuDescription;
    private string? _operationError;
    private bool _operationCanRetry;
    private string? _muscleNameError;
    private string? _exerciseNameError;
    private string? _exercisePrimaryMuscleError;
    private string? _menuNameError;
    private string? _menuExerciseError;
    private string _exerciseSearchText = string.Empty;
    private bool _isBusy;
    private bool _initialized;

    public MasterDataViewModel(MasterDataService service, ActiveWorkoutViewModel workout, IUiExceptionReporter? errors = null)
    {
        _service = service; _workout = workout; _errors = errors ?? NullUiExceptionReporter.Instance;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync);
        NewMuscleGroupCommand = new RelayCommand(NewMuscleGroup);
        SaveMuscleGroupCommand = new AsyncRelayCommand(SaveMuscleGroupAsync);
        MoveMuscleUpCommand = new AsyncRelayCommand(() => MoveMuscleAsync(-1), () => SelectedMuscleGroup is not null);
        MoveMuscleDownCommand = new AsyncRelayCommand(() => MoveMuscleAsync(1), () => SelectedMuscleGroup is not null);
        NewExerciseCommand = new RelayCommand(NewExercise);
        SaveExerciseCommand = new AsyncRelayCommand(SaveExerciseAsync);
        NewMenuCommand = new RelayCommand(NewMenu);
        SaveMenuCommand = new AsyncRelayCommand(SaveMenuAsync);
        AddMenuExerciseCommand = new RelayCommand(AddMenuExercise);
        RemoveMenuExerciseCommand = new RelayCommand<MenuExerciseEditorViewModel>(RemoveMenuExercise);
        MoveMenuExerciseUpCommand = new RelayCommand<MenuExerciseEditorViewModel>(x => MoveMenuExercise(x, -1));
        MoveMenuExerciseDownCommand = new RelayCommand<MenuExerciseEditorViewModel>(x => MoveMenuExercise(x, 1));
    }

    public ObservableCollection<MuscleGroupItemViewModel> MuscleGroups { get; } = [];
    public ObservableCollection<MuscleChoiceViewModel> SecondaryMuscles { get; } = [];
    public ObservableCollection<ExerciseMasterItemViewModel> Exercises { get; } = [];
    public ObservableCollection<MenuMasterItemViewModel> Menus { get; } = [];
    public ObservableCollection<MenuExerciseEditorViewModel> MenuExercises { get; } = [];
    public IEnumerable<ExerciseMasterItemViewModel> FilteredExercises => FilterExercises(Exercises);
    public IEnumerable<ExerciseMasterItemViewModel> ActiveExercises => FilterExercises(Exercises.Where(x => x.IsActive));
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string? OperationError { get => _operationError; private set { SetProperty(ref _operationError, value); OnPropertyChanged(nameof(HasError)); } }
    public bool OperationCanRetry { get => _operationCanRetry; private set => SetProperty(ref _operationCanRetry, value); }
    public string? MuscleNameError { get => _muscleNameError; private set => SetProperty(ref _muscleNameError, value); }
    public string? ExerciseNameError { get => _exerciseNameError; private set => SetProperty(ref _exerciseNameError, value); }
    public string? ExercisePrimaryMuscleError { get => _exercisePrimaryMuscleError; private set => SetProperty(ref _exercisePrimaryMuscleError, value); }
    public string? MenuNameError { get => _menuNameError; private set => SetProperty(ref _menuNameError, value); }
    public string? MenuExerciseError { get => _menuExerciseError; private set => SetProperty(ref _menuExerciseError, value); }
    public string ExerciseSearchText { get => _exerciseSearchText; set { if (SetProperty(ref _exerciseSearchText, value)) { OnPropertyChanged(nameof(FilteredExercises)); OnPropertyChanged(nameof(ActiveExercises)); } } }
    public bool HasError => !string.IsNullOrWhiteSpace(OperationError);
    public string PageState => IsBusy ? "Loading" : HasError ? "Error" : Menus.Count == 0 && Exercises.Count == 0 ? "Empty" : "Loaded";

    public MuscleGroupItemViewModel? SelectedMuscleGroup
    {
        get => _selectedMuscleGroup;
        set { if (!SetProperty(ref _selectedMuscleGroup, value)) return; MuscleName = value?.Name ?? string.Empty; RaiseMoveMuscleCommands(); OnPropertyChanged(nameof(MuscleEditorTitle)); }
    }
    public string MuscleName { get => _muscleName; set { if (SetProperty(ref _muscleName, value)) MuscleNameError = null; } }
    public string MuscleEditorTitle => SelectedMuscleGroup is null ? "新しい部位" : "部位を編集";
    public ExerciseMasterItemViewModel? SelectedExercise
    {
        get => _selectedExercise;
        set { if (!SetProperty(ref _selectedExercise, value)) return; PopulateExerciseEditor(value); OnPropertyChanged(nameof(ExerciseEditorTitle)); }
    }
    public string ExerciseEditorTitle => SelectedExercise is null ? "新しい種目" : "種目を編集";
    public string ExerciseName { get => _exerciseName; set { if (SetProperty(ref _exerciseName, value)) ExerciseNameError = null; } }
    public string? ExerciseNote { get => _exerciseNote; set => SetProperty(ref _exerciseNote, value); }
    public bool ExerciseIsActive { get => _exerciseIsActive; set => SetProperty(ref _exerciseIsActive, value); }
    public MuscleGroupItemViewModel? ExercisePrimaryMuscle { get => _exercisePrimaryMuscle; set { if (SetProperty(ref _exercisePrimaryMuscle, value)) ExercisePrimaryMuscleError = null; } }
    public MenuMasterItemViewModel? SelectedMenu
    {
        get => _selectedMenu;
        set { if (!SetProperty(ref _selectedMenu, value)) return; PopulateMenuEditor(value); OnPropertyChanged(nameof(MenuEditorTitle)); }
    }
    public string MenuEditorTitle => SelectedMenu is null ? "新しいメニュー" : "メニューを編集";
    public string MenuName { get => _menuName; set { if (SetProperty(ref _menuName, value)) MenuNameError = null; } }
    public string? MenuDescription { get => _menuDescription; set => SetProperty(ref _menuDescription, value); }
    public ExerciseMasterItemViewModel? ExerciseToAdd { get => _exerciseToAdd; set => SetProperty(ref _exerciseToAdd, value); }

    public ICommand RefreshCommand { get; }
    public ICommand NewMuscleGroupCommand { get; }
    public ICommand SaveMuscleGroupCommand { get; }
    public AsyncRelayCommand MoveMuscleUpCommand { get; }
    public AsyncRelayCommand MoveMuscleDownCommand { get; }
    public ICommand NewExerciseCommand { get; }
    public ICommand SaveExerciseCommand { get; }
    public ICommand NewMenuCommand { get; }
    public ICommand SaveMenuCommand { get; }
    public ICommand AddMenuExerciseCommand { get; }
    public ICommand RemoveMenuExerciseCommand { get; }
    public ICommand MoveMenuExerciseUpCommand { get; }
    public ICommand MoveMenuExerciseDownCommand { get; }
    public event EventHandler? WorkoutStarted;

    public async Task InitializeAsync() { if (_initialized) return; await RefreshAsync(); _initialized = true; }
    public async Task RefreshAsync()
    {
        await RunAsync(async () =>
        {
            var musclesTask = _service.GetMuscleGroupsAsync(); var exercisesTask = _service.GetExercisesAsync(true); var menusTask = _service.GetMenusAsync();
            await Task.WhenAll(musclesTask, exercisesTask, menusTask);
            Replace(MuscleGroups, (await musclesTask).Select(x => new MuscleGroupItemViewModel(x)));
            Replace(Exercises, (await exercisesTask).Select(x => new ExerciseMasterItemViewModel(x)));
            Replace(Menus, (await menusTask).Select(x => new MenuMasterItemViewModel(x)));
            OnPropertyChanged(nameof(FilteredExercises)); OnPropertyChanged(nameof(ActiveExercises)); OnPropertyChanged(nameof(PageState)); NewMuscleGroup(); NewExercise(); NewMenu();
        });
    }

    public async Task<ExerciseDeleteResult> DeleteSelectedExerciseAsync()
    {
        if (SelectedExercise is null) return ExerciseDeleteResult.NotFound;
        var id = SelectedExercise.Id; var result = ExerciseDeleteResult.NotFound;
        await RunAsync(async () => { result = await _service.DeleteExerciseAsync(id); if (result == ExerciseDeleteResult.Deleted) await ReloadExercisesAndMenusAsync(); });
        return result;
    }
    public async Task SetSelectedExerciseActiveAsync(bool active)
    {
        if (SelectedExercise is null) return; var id = SelectedExercise.Id;
        await RunAsync(async () => { await _service.SetExerciseActiveAsync(id, active); await ReloadExercisesAndMenusAsync(); SelectedExercise = Exercises.Single(x => x.Id == id); });
    }
    public async Task<MuscleGroupDeleteResult> DeleteSelectedMuscleGroupAsync()
    {
        if (SelectedMuscleGroup is null) return MuscleGroupDeleteResult.NotFound; var result = MuscleGroupDeleteResult.NotFound;
        await RunAsync(async () => { result = await _service.DeleteMuscleGroupAsync(SelectedMuscleGroup.Id); if (result == MuscleGroupDeleteResult.Deleted) await ReloadMusclesAndExercisesAsync(); }); return result;
    }
    public async Task DeleteMenuAsync(MenuMasterItemViewModel menu) => await RunAsync(async () => { await _service.DeleteMenuAsync(menu.Id); await ReloadMenusAsync(); });
    public async Task DuplicateMenuAsync(MenuMasterItemViewModel menu)
    {
        await RunAsync(async () => { var copy = await _service.DuplicateMenuAsync(menu.Id, DuplicateName(menu.Name)); await ReloadMenusAsync(); SelectedMenu = Menus.Single(x => x.Id == copy.Id); });
    }
    public async Task StartWorkoutAsync(MenuMasterItemViewModel menu)
    {
        await RunAsync(async () => { await _workout.StartAsync(menu.Id, DateOnly.FromDateTime(DateTime.Today)); WorkoutStarted?.Invoke(this, EventArgs.Empty); });
    }
    public void MoveMenuExercise(MenuExerciseEditorViewModel item, int delta)
    {
        var oldIndex = MenuExercises.IndexOf(item); MoveMenuExercise(oldIndex, oldIndex + delta);
    }
    public void MoveMenuExercise(int oldIndex, int newIndex)
    {
        if (oldIndex < 0 || newIndex < 0 || oldIndex >= MenuExercises.Count || newIndex >= MenuExercises.Count || oldIndex == newIndex) return;
        MenuExercises.Move(oldIndex, newIndex);
    }

    public async Task SaveMuscleGroupAsync()
    {
        if (!ValidateText(MuscleName, 50, "部位名", error => MuscleNameError = error)) return;
        await RunAsync(async () => { var saved = await _service.SaveMuscleGroupAsync(new(SelectedMuscleGroup?.Id, MuscleName, SelectedMuscleGroup?.DisplayOrder ?? MuscleGroups.Count)); await ReloadMusclesAndExercisesAsync(); SelectedMuscleGroup = MuscleGroups.Single(x => x.Id == saved.Id); });
    }
    private async Task MoveMuscleAsync(int delta)
    {
        if (SelectedMuscleGroup is null) return; var old = MuscleGroups.IndexOf(SelectedMuscleGroup); var next = old + delta; if (next < 0 || next >= MuscleGroups.Count) return;
        var id = SelectedMuscleGroup.Id; MuscleGroups.Move(old, next); await RunAsync(async () => { await _service.ReorderMuscleGroupsAsync(MuscleGroups.Select(x => x.Id).ToList()); await ReloadMusclesAndExercisesAsync(); SelectedMuscleGroup = MuscleGroups.Single(x => x.Id == id); });
    }
    public async Task SaveExerciseAsync()
    {
        if (!ValidateText(ExerciseName, 100, "種目名", error => ExerciseNameError = error)) return;
        if (ExercisePrimaryMuscle is null) { ExercisePrimaryMuscleError = "メイン部位を選択してください。"; return; }
        await RunAsync(async () =>
        {
            var saved = await _service.SaveExerciseAsync(new(SelectedExercise?.Id, ExerciseName, ExercisePrimaryMuscle.Id, SecondaryMuscles.Where(x => x.IsSelected).Select(x => x.Muscle.Id).ToList(), ExerciseNote, ExerciseIsActive));
            await ReloadExercisesAndMenusAsync(); SelectedExercise = Exercises.Single(x => x.Id == saved.Id);
        });
    }
    public async Task SaveMenuAsync()
    {
        if (!ValidateText(MenuName, 100, "メニュー名", error => MenuNameError = error)) return;
        if (MenuExercises.Any(x => x.DefaultSetCountError is not null)) { MenuExerciseError = "予定セット数を確認してください。"; return; }
        MenuExerciseError = null;
        await RunAsync(async () =>
        {
            var request = new SaveWorkoutMenuRequest(SelectedMenu?.Id, MenuName, MenuDescription, MenuExercises.Select(x => new SaveMenuExerciseRequest(x.Id, x.ExerciseId, x.DefaultSetCount, x.Note)).ToList());
            var saved = await _service.SaveMenuAsync(request); await ReloadMenusAsync(); SelectedMenu = Menus.Single(x => x.Id == saved.Id);
        });
    }
    private void AddMenuExercise()
    {
        if (ExerciseToAdd is null || MenuExercises.Any(x => x.ExerciseId == ExerciseToAdd.Id)) return;
        MenuExercises.Add(new(null, ExerciseToAdd, 3, null)); ExerciseToAdd = null;
    }
    private void RemoveMenuExercise(MenuExerciseEditorViewModel item) => MenuExercises.Remove(item);
    private void NewMuscleGroup() { SelectedMuscleGroup = null; MuscleName = string.Empty; MuscleNameError = null; OnPropertyChanged(nameof(MuscleEditorTitle)); }
    private void NewExercise() { SelectedExercise = null; ExerciseName = string.Empty; ExerciseNote = null; ExerciseIsActive = true; ExercisePrimaryMuscle = MuscleGroups.FirstOrDefault(); ExerciseNameError = null; ExercisePrimaryMuscleError = null; RebuildSecondaryChoices([]); OnPropertyChanged(nameof(ExerciseEditorTitle)); }
    private void NewMenu() { SelectedMenu = null; MenuName = string.Empty; MenuDescription = null; MenuExercises.Clear(); MenuNameError = null; MenuExerciseError = null; OnPropertyChanged(nameof(MenuEditorTitle)); }
    private void PopulateExerciseEditor(ExerciseMasterItemViewModel? item)
    {
        if (item is null) return; ExerciseName = item.Dto.Name; ExerciseNote = item.Dto.Note; ExerciseIsActive = item.Dto.IsActive;
        ExercisePrimaryMuscle = MuscleGroups.SingleOrDefault(x => x.Id == item.Dto.PrimaryMuscleId); RebuildSecondaryChoices(item.Dto.SecondaryMuscleIds);
    }
    private void PopulateMenuEditor(MenuMasterItemViewModel? item)
    {
        MenuExercises.Clear(); if (item is null) return; MenuName = item.Dto.Name; MenuDescription = item.Dto.Description;
        foreach (var row in item.Dto.Exercises) { var exercise = Exercises.SingleOrDefault(x => x.Id == row.ExerciseId); if (exercise is not null) MenuExercises.Add(new(row.Id, exercise, row.DefaultSetCount, row.Note)); }
    }
    private void RebuildSecondaryChoices(IEnumerable<Guid> selected)
    {
        var ids = selected.ToHashSet(); SecondaryMuscles.Clear(); foreach (var muscle in MuscleGroups) SecondaryMuscles.Add(new(muscle) { IsSelected = ids.Contains(muscle.Id) });
    }
    private async Task ReloadMusclesAndExercisesAsync()
    {
        var muscles = await _service.GetMuscleGroupsAsync(); Replace(MuscleGroups, muscles.Select(x => new MuscleGroupItemViewModel(x))); await ReloadExercisesAndMenusAsync();
    }
    private async Task ReloadExercisesAndMenusAsync()
    {
        var exercises = await _service.GetExercisesAsync(true); Replace(Exercises, exercises.Select(x => new ExerciseMasterItemViewModel(x))); OnPropertyChanged(nameof(FilteredExercises)); OnPropertyChanged(nameof(ActiveExercises)); await ReloadMenusAsync();
    }
    private async Task ReloadMenusAsync() { var menus = await _service.GetMenusAsync(); Replace(Menus, menus.Select(x => new MenuMasterItemViewModel(x))); }
    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy) return; IsBusy = true; OperationError = null; OperationCanRetry = false; OnPropertyChanged(nameof(PageState));
        try { await operation(); }
        catch (DomainValidationException) { OperationError = "入力内容を確認してください。"; OperationCanRetry = false; }
        catch (Exception ex) { _errors.Report(ex, "MasterData.Operation"); OperationError = "操作を完了できませんでした。再試行してください。"; OperationCanRetry = true; }
        finally { IsBusy = false; OnPropertyChanged(nameof(PageState)); }
    }
    private void RaiseMoveMuscleCommands() { MoveMuscleUpCommand.RaiseCanExecuteChanged(); MoveMuscleDownCommand.RaiseCanExecuteChanged(); }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values) { collection.Clear(); foreach (var value in values) collection.Add(value); }
    private static string DuplicateName(string name) { const string suffix = " のコピー"; return name.Length + suffix.Length <= 100 ? name + suffix : name[..(100 - suffix.Length)] + suffix; }
    private static bool ValidateText(string value, int maximum, string label, Action<string?> setError)
    {
        var length = value?.Trim().Length ?? 0;
        if (length == 0) { setError($"{label}を入力してください。"); return false; }
        if (length > maximum) { setError($"{label}は1〜{maximum}文字で入力してください。"); return false; }
        setError(null); return true;
    }
    private IEnumerable<ExerciseMasterItemViewModel> FilterExercises(IEnumerable<ExerciseMasterItemViewModel> source)
    {
        var term = ExerciseSearchText.Trim(); return string.IsNullOrEmpty(term) ? source : source.Where(x => x.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase) || x.PrimaryMuscleName.Contains(term, StringComparison.CurrentCultureIgnoreCase) || x.SecondaryMuscles.Contains(term, StringComparison.CurrentCultureIgnoreCase));
    }
}
