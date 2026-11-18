using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;

namespace WorkoutTracker.App.ViewModels;

public sealed class HistoryItemViewModel(WorkoutHistoryItem item)
{
    public WorkoutHistoryItem Item { get; } = item;
    public Guid Id => Item.Id;
    public string Date => Item.WorkoutDate.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);
    public string Name => string.IsNullOrWhiteSpace(Item.Name) ? "ワークアウト" : Item.Name;
    public string Menu => Item.SourceMenuName ?? "メニューなし";
    public decimal TotalVolume => Item.TotalVolume;
    public int TotalSets => Item.TotalSets;
    public int ExerciseCount => Item.ExerciseCount;
}

public sealed class HistoryViewModel : ObservableObject
{
    private readonly HistoryCalendarService _service;
    private readonly IUiExceptionReporter _errors;
    private readonly Stack<HistoryCursor?> _cursorStack = [];
    private CancellationTokenSource? _queryCancellation;
    private long _operationId;
    private HistoryCursor? _currentCursor;
    private HistoryCursor? _nextCursor;
    private DateTime? _fromDate;
    private DateTime? _toDateInclusive;
    private HistoryFilterOption? _selectedExercise;
    private HistoryFilterOption? _selectedMuscle;
    private HistoryFilterOption? _selectedMenu;
    private int _pageSize = 50;
    private bool _isLoading;
    private bool _initialized;
    private bool _optionsLoaded;
    private string? _errorMessage;
    private WorkoutDetailDto? _selectedDetail;
    private HistoryItemViewModel? _selectedItem;
    private readonly ChangeCompletedWorkoutDateUseCase? _dateEditor;
    private DateTime? _selectedWorkoutDate;
    private bool _isEditingWorkoutDate;
    private string? _dateEditError;

    public HistoryViewModel(HistoryCalendarService service, IUiExceptionReporter? errors = null, ChangeCompletedWorkoutDateUseCase? dateEditor = null)
    {
        _service = service; _errors = errors ?? NullUiExceptionReporter.Instance; _dateEditor = dateEditor; ApplyFiltersCommand = new AsyncRelayCommand(ApplyFiltersAsync); NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => CanNextPage); PreviousPageCommand = new AsyncRelayCommand(PreviousPageAsync, () => CanPreviousPage); RetryCommand = new AsyncRelayCommand(RetryAsync);
        OpenDetailCommand = new AsyncRelayCommand<HistoryItemViewModel>(OpenDetailAsync); CloseDetailCommand = new RelayCommand(CloseDetail); ClearFiltersCommand = new AsyncRelayCommand(ClearFiltersAsync);
        EditWorkoutDateCommand = new RelayCommand(BeginWorkoutDateEdit, () => SelectedDetail is not null && !IsEditingWorkoutDate);
        SaveWorkoutDateCommand = new AsyncRelayCommand(SaveWorkoutDateAsync, () => SelectedDetail is not null && IsEditingWorkoutDate && SelectedWorkoutDate.HasValue);
        CancelWorkoutDateCommand = new RelayCommand(CancelWorkoutDateEdit, () => IsEditingWorkoutDate);
    }
    public ObservableCollection<HistoryItemViewModel> Items { get; } = [];
    public ObservableCollection<HistoryFilterOption> ExerciseOptions { get; } = [];
    public ObservableCollection<HistoryFilterOption> MuscleOptions { get; } = [];
    public ObservableCollection<HistoryFilterOption> MenuOptions { get; } = [];
    public IReadOnlyList<int> PageSizeOptions { get; } = [25, 50, 100];
    public DateTime? FromDate { get => _fromDate; set { if (SetProperty(ref _fromDate, value)) ResetForFilterChange(); } }
    public DateTime? ToDateInclusive { get => _toDateInclusive; set { if (SetProperty(ref _toDateInclusive, value)) ResetForFilterChange(); } }
    public HistoryFilterOption? SelectedExercise { get => _selectedExercise; set { if (SetProperty(ref _selectedExercise, value)) ResetForFilterChange(); } }
    public HistoryFilterOption? SelectedMuscle { get => _selectedMuscle; set { if (SetProperty(ref _selectedMuscle, value)) ResetForFilterChange(); } }
    public HistoryFilterOption? SelectedMenu { get => _selectedMenu; set { if (SetProperty(ref _selectedMenu, value)) ResetForFilterChange(); } }
    public int PageSize { get => _pageSize; set { if (!PageSizeOptions.Contains(value)) throw new ArgumentOutOfRangeException(nameof(value)); if (SetProperty(ref _pageSize, value)) ResetForFilterChange(); } }
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(PageState)); } }
    public string? ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(PageState)); } }
    public string PageState => IsLoading ? "Loading" : ErrorMessage is not null ? "Error" : Items.Count == 0 ? "Empty" : "Loaded";
    public bool IsEmpty => !IsLoading && ErrorMessage is null && Items.Count == 0;
    public WorkoutDetailDto? SelectedDetail { get => _selectedDetail; private set { if (SetProperty(ref _selectedDetail, value)) { OnPropertyChanged(nameof(IsDrawerOpen)); OnPropertyChanged(nameof(CanEditWorkoutDate)); EditWorkoutDateCommand.RaiseCanExecuteChanged(); SaveWorkoutDateCommand.RaiseCanExecuteChanged(); } } }
    public DateTime? SelectedWorkoutDate { get => _selectedWorkoutDate; set { if (SetProperty(ref _selectedWorkoutDate, value)) SaveWorkoutDateCommand.RaiseCanExecuteChanged(); } }
    public bool IsEditingWorkoutDate { get => _isEditingWorkoutDate; private set { if (SetProperty(ref _isEditingWorkoutDate, value)) { EditWorkoutDateCommand.RaiseCanExecuteChanged(); SaveWorkoutDateCommand.RaiseCanExecuteChanged(); CancelWorkoutDateCommand.RaiseCanExecuteChanged(); } } }
    public string? DateEditError { get => _dateEditError; private set => SetProperty(ref _dateEditError, value); }
    public bool CanEditWorkoutDate => SelectedDetail is not null && !IsEditingWorkoutDate;
    public HistoryItemViewModel? SelectedItem { get => _selectedItem; set => SetProperty(ref _selectedItem, value); }
    public bool IsDrawerOpen => SelectedDetail is not null;
    public bool CanNextPage => _nextCursor is not null && !IsLoading;
    public bool CanPreviousPage => _cursorStack.Count > 0 && !IsLoading;
    public int PageNumber => _cursorStack.Count + 1;
    public HistoryCursor? CurrentCursor => _currentCursor;
    public ICommand ApplyFiltersCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public RelayCommand EditWorkoutDateCommand { get; }
    public AsyncRelayCommand SaveWorkoutDateCommand { get; }
    public RelayCommand CancelWorkoutDateCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized) return; _initialized = true;
        try
        {
            var options = await _service.GetHistoryFilterOptionsAsync(); Replace(ExerciseOptions, options.Exercises); Replace(MuscleOptions, options.PrimaryMuscles); Replace(MenuOptions, options.Menus);
            _optionsLoaded = true;
            await LoadPageAsync(null);
        }
        catch (Exception ex) { ReportFailure(ex, "History.Initialize"); IsLoading = false; }
    }
    public async Task ReloadAsync()
    {
        CancelCurrentQuery(); _initialized = false; _optionsLoaded = false; ExerciseOptions.Clear(); MuscleOptions.Clear(); MenuOptions.Clear(); Items.Clear();
        _cursorStack.Clear(); _currentCursor = null; _nextCursor = null; SelectedDetail = null; SelectedItem = null;
        await InitializeAsync();
    }
    public async Task RetryAsync()
    {
        if (!_optionsLoaded) { _initialized = false; await InitializeAsync(); }
        else await LoadPageAsync(_currentCursor);
    }
    public Task ApplyFiltersAsync() { _cursorStack.Clear(); _currentCursor = null; return LoadPageAsync(null); }
    public async Task NextPageAsync()
    {
        if (_nextCursor is null) return; _cursorStack.Push(_currentCursor); _currentCursor = _nextCursor; OnPropertyChanged(nameof(CurrentCursor)); await LoadPageAsync(_currentCursor);
    }
    public async Task PreviousPageAsync()
    {
        if (_cursorStack.Count == 0) return; _currentCursor = _cursorStack.Pop(); OnPropertyChanged(nameof(CurrentCursor)); await LoadPageAsync(_currentCursor);
    }
    public async Task OpenDetailAsync(HistoryItemViewModel item)
    {
        var operation = Interlocked.Increment(ref _operationId); CancelCurrentQuery(); var cts = _queryCancellation = new();
        try { var detail = await _service.GetWorkoutDetailAsync(item.Id, cts.Token); if (operation == _operationId) { SelectedItem = item; SelectedDetail = detail; SelectedWorkoutDate = detail?.WorkoutDate.ToDateTime(TimeOnly.MinValue); IsEditingWorkoutDate = false; DateEditError = null; } }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { if (operation == _operationId) ReportFailure(ex, "History.Detail"); }
    }
    public void CloseDetail() { SelectedDetail = null; IsEditingWorkoutDate = false; DateEditError = null; }
    public void BeginWorkoutDateEdit() { if (SelectedDetail is null) return; DateEditError = null; SelectedWorkoutDate = SelectedDetail.WorkoutDate.ToDateTime(TimeOnly.MinValue); IsEditingWorkoutDate = true; }
    public void CancelWorkoutDateEdit() { if (SelectedDetail is not null) SelectedWorkoutDate = SelectedDetail.WorkoutDate.ToDateTime(TimeOnly.MinValue); DateEditError = null; IsEditingWorkoutDate = false; }
    public async Task SaveWorkoutDateAsync()
    {
        if (SelectedDetail is null || !SelectedWorkoutDate.HasValue || _dateEditor is null) return;
        var sessionId = SelectedDetail.WorkoutSessionId;
        try
        {
            var saved = await _dateEditor.ExecuteAsync(sessionId, DateOnly.FromDateTime(SelectedWorkoutDate.Value));
            if (!saved) { DateEditError = "対象の完了記録が見つかりません。履歴を再読み込みしてください。"; return; }
            DateEditError = null; IsEditingWorkoutDate = false;
            await LoadPageAsync(null, keepDetail: true);
            SelectedDetail = await _service.GetWorkoutDetailAsync(sessionId);
            SelectedItem = Items.FirstOrDefault(x => x.Id == sessionId);
            if (SelectedDetail is not null) SelectedWorkoutDate = SelectedDetail.WorkoutDate.ToDateTime(TimeOnly.MinValue);
        }
        catch (ArgumentOutOfRangeException) { DateEditError = "実施日は1900/01/01から今日までの日付を選択してください。"; }
        catch (Exception ex) { _errors.Report(ex, "History.ChangeWorkoutDate"); DateEditError = "保存処理中にエラーが発生しました。履歴を再読み込みして確認してください。"; }
    }
    public async Task ClearFiltersAsync()
    {
        _fromDate = null; _toDateInclusive = null; _selectedExercise = null; _selectedMuscle = null; _selectedMenu = null;
        OnPropertyChanged(nameof(FromDate)); OnPropertyChanged(nameof(ToDateInclusive)); OnPropertyChanged(nameof(SelectedExercise)); OnPropertyChanged(nameof(SelectedMuscle)); OnPropertyChanged(nameof(SelectedMenu)); ResetForFilterChange(); await ApplyFiltersAsync();
    }

    private async Task LoadPageAsync(HistoryCursor? cursor, bool keepDetail = false)
    {
        var operation = Interlocked.Increment(ref _operationId); CancelCurrentQuery(); var cts = _queryCancellation = new(); IsLoading = true; ErrorMessage = null; if (!keepDetail) CloseDetail();
        try
        {
            var request = new HistoryQuery(FromDate is null ? null : DateOnly.FromDateTime(FromDate.Value), ToDateInclusive is null ? null : DateOnly.FromDateTime(ToDateInclusive.Value).AddDays(1), SelectedExercise?.Id, SelectedMuscle?.Id, SelectedMenu?.Id, PageSize, cursor);
            var page = await _service.GetHistoryPageAsync(request, cts.Token); if (operation != _operationId) return;
            Replace(Items, page.Items.Select(x => new HistoryItemViewModel(x))); _nextCursor = page.NextCursor; OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(PageState)); RaisePaging();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { if (operation == _operationId) ReportFailure(ex, "History.Page"); }
        finally { if (operation == _operationId) { IsLoading = false; OnPropertyChanged(nameof(IsEmpty)); RaisePaging(); } }
    }
    private void ResetForFilterChange()
    {
        CancelCurrentQuery(); _cursorStack.Clear(); _currentCursor = null; _nextCursor = null; CloseDetail(); SelectedItem = null; OnPropertyChanged(nameof(CurrentCursor)); OnPropertyChanged(nameof(PageNumber)); RaisePaging();
    }
    private void RaisePaging() { OnPropertyChanged(nameof(CanNextPage)); OnPropertyChanged(nameof(CanPreviousPage)); OnPropertyChanged(nameof(PageNumber)); NextPageCommand.RaiseCanExecuteChanged(); PreviousPageCommand.RaiseCanExecuteChanged(); }
    private void CancelCurrentQuery() { _queryCancellation?.Cancel(); _queryCancellation?.Dispose(); _queryCancellation = null; }
    private void ReportFailure(Exception exception, string operation) { _errors.Report(exception, operation); ErrorMessage = "履歴の読み込みに失敗しました。再試行してください。"; }
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
}

public sealed class CalendarDayViewModel(CalendarDayDto? day, PlannedDayDto? plan = null)
{
    public CalendarDayDto? Day { get; } = day;
    public PlannedDayDto? Plan { get; } = plan;
    public DateOnly? Date => Day?.Date;
    public string DayLabel => Date?.Day.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    public bool HasWorkouts => Day?.HasWorkouts == true;
    public int WorkoutCount => Day?.WorkoutCount ?? 0;
    public string Marker => WorkoutCount == 0 ? string.Empty : $"● {WorkoutCount}";
    public string PlanMarker => Plan is null ? string.Empty : Plan.IsRest ? "休" : Plan.MenuId is null ? string.Empty : "予定";
    public bool HasPlan => PlanMarker.Length > 0;
    public string PlanLabel => Plan is null ? "予定なし" : Plan.IsRest ? "休息日" : Plan.MenuName ?? "予定なし";
}

public sealed class CalendarViewModel : ObservableObject
{
    private readonly HistoryCalendarService _service;
    private readonly IUiExceptionReporter _errors;
    private readonly IWeeklyScheduleService? _schedule;
    private readonly ActiveWorkoutViewModel? _workout;
    private CancellationTokenSource? _queryCancellation;
    private long _operationId;
    private DateOnly _currentMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    private CalendarDayViewModel? _selectedDay;
    private WorkoutDetailDto? _selectedDetail;
    private bool _isLoading;
    private bool _initialized;
    private string? _errorMessage;
    private string? _planStatus;
    public CalendarViewModel(HistoryCalendarService service, IUiExceptionReporter? errors = null, IWeeklyScheduleService? schedule = null, ActiveWorkoutViewModel? workout = null)
    {
        _service = service; _errors = errors ?? NullUiExceptionReporter.Instance; _schedule = schedule; _workout = workout; PreviousMonthCommand = new AsyncRelayCommand(() => ChangeMonthAsync(-1)); NextMonthCommand = new AsyncRelayCommand(() => ChangeMonthAsync(1)); SelectDayCommand = new RelayCommand<CalendarDayViewModel>(SelectDay, x => x.Date.HasValue); OpenDetailCommand = new AsyncRelayCommand<CalendarWorkoutSummaryDto>(OpenDetailAsync); CloseDetailCommand = new RelayCommand(() => SelectedDetail = null); RetryCommand = new AsyncRelayCommand(() => LoadMonthAsync(_currentMonth)); StartPlannedCommand = new AsyncRelayCommand(StartPlannedAsync);
    }
    public ObservableCollection<CalendarDayViewModel> Cells { get; } = [];
    public CalendarDayViewModel? SelectedDay { get => _selectedDay; private set { if (SetProperty(ref _selectedDay, value)) { OnPropertyChanged(nameof(SelectedWorkouts)); OnPropertyChanged(nameof(SelectedDayTitle)); OnPropertyChanged(nameof(IsSelectedDayEmpty)); OnPropertyChanged(nameof(SelectedPlanLabel)); OnPropertyChanged(nameof(CanStartPlanned)); } } }
    public IReadOnlyList<CalendarWorkoutSummaryDto> SelectedWorkouts => SelectedDay?.Day?.Workouts ?? [];
    public string SelectedDayTitle => SelectedDay?.Date?.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) ?? "日付を選択してください";
    public bool IsSelectedDayEmpty => SelectedDay?.Date is not null && SelectedWorkouts.Count == 0;
    public string SelectedPlanLabel => SelectedDay?.PlanLabel ?? "日付を選択してください";
    public bool CanStartPlanned => SelectedDay?.Plan?.MenuId is not null && SelectedDay.Plan.IsRest == false && SelectedDay.Date <= DateOnly.FromDateTime(DateTime.Today);
    public string? PlanStatus { get => _planStatus; private set => SetProperty(ref _planStatus, value); }
    public DateOnly CurrentMonth => _currentMonth;
    public string MonthTitle => _currentMonth.ToString("yyyy年 M月", CultureInfo.CurrentCulture);
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) OnPropertyChanged(nameof(PageState)); } }
    public string? ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) OnPropertyChanged(nameof(PageState)); } }
    public string PageState => IsLoading ? "Loading" : ErrorMessage is not null ? "Error" : Cells.Where(x => x.Date.HasValue).All(x => !x.HasWorkouts) ? "Empty" : "Loaded";
    public bool IsMonthEmpty => !IsLoading && ErrorMessage is null && Cells.Where(x => x.Date.HasValue).All(x => !x.HasWorkouts);
    public WorkoutDetailDto? SelectedDetail { get => _selectedDetail; private set { if (SetProperty(ref _selectedDetail, value)) OnPropertyChanged(nameof(IsDrawerOpen)); } }
    public bool IsDrawerOpen => SelectedDetail is not null;
    public ICommand PreviousMonthCommand { get; }
    public ICommand NextMonthCommand { get; }
    public ICommand SelectDayCommand { get; }
    public ICommand OpenDetailCommand { get; }
    public ICommand CloseDetailCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand StartPlannedCommand { get; }
    public event EventHandler? WorkoutStarted;
    public async Task InitializeAsync() { if (_initialized) return; _initialized = true; await LoadMonthAsync(_currentMonth); }
    public async Task ReloadAsync() { SelectedDay = null; SelectedDetail = null; await LoadMonthAsync(_currentMonth); }
    public Task ChangeMonthAsync(int months) { _currentMonth = _currentMonth.AddMonths(months); OnPropertyChanged(nameof(CurrentMonth)); OnPropertyChanged(nameof(MonthTitle)); SelectedDay = null; SelectedDetail = null; return LoadMonthAsync(_currentMonth); }
    public void SelectDay(CalendarDayViewModel day) { if (day.Date.HasValue) { SelectedDay = day; SelectedDetail = null; PlanStatus = null; } }
    private async Task StartPlannedAsync()
    {
        if (!CanStartPlanned || SelectedDay?.Plan?.MenuId is not Guid menuId || _workout is null || SelectedDay.Date is not DateOnly date) return;
        if (_workout.IsActive) { PlanStatus = "進行中の記録を先に完了してください。"; return; }
        try
        {
            var result = await _workout.StartAsync(menuId, date);
            if (result is StartWorkoutResult.Started) WorkoutStarted?.Invoke(this, EventArgs.Empty);
            else PlanStatus = "進行中の記録を先に確認してください。";
        }
        catch (Exception ex) { _errors.Report(ex, "Calendar.StartPlanned"); PlanStatus = "予定から記録を開始できませんでした。"; }
    }
    public async Task OpenDetailAsync(CalendarWorkoutSummaryDto workout)
    {
        var operation = Interlocked.Increment(ref _operationId); var monthAtRequest = _currentMonth; try { var detail = await _service.GetWorkoutDetailAsync(workout.WorkoutSessionId); if (operation == _operationId && monthAtRequest == _currentMonth) SelectedDetail = detail; } catch (Exception ex) { if (operation == _operationId) ReportFailure(ex, "Calendar.Detail"); }
    }
    private async Task LoadMonthAsync(DateOnly month)
    {
        var operation = Interlocked.Increment(ref _operationId); _queryCancellation?.Cancel(); _queryCancellation?.Dispose(); var cts = _queryCancellation = new(); IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await _service.GetCalendarMonthAsync(month.Year, month.Month, cts.Token); if (operation != _operationId || month != _currentMonth) return;
            var plans = new Dictionary<DateOnly, PlannedDayDto>();
            if (_schedule is not null)
            {
                var start = result.Days[0].Date;
                var last = result.Days[^1].Date;
                for (var date = start; date <= last; date = date.AddDays(7))
                    foreach (var plan in await _schedule.GetWeekAsync(date, cts.Token)) plans[plan.Date] = plan;
            }
            if (operation != _operationId || month != _currentMonth) return;
            Cells.Clear(); var leading = (int)result.Days[0].Date.DayOfWeek; for (var i = 0; i < leading; i++) Cells.Add(new(null)); foreach (var day in result.Days) Cells.Add(new(day, plans.GetValueOrDefault(day.Date))); OnPropertyChanged(nameof(PageState)); OnPropertyChanged(nameof(IsMonthEmpty));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { if (operation == _operationId) ReportFailure(ex, "Calendar.Month"); }
        finally { if (operation == _operationId) { IsLoading = false; OnPropertyChanged(nameof(IsMonthEmpty)); } }
    }
    private void ReportFailure(Exception exception, string operation) { _errors.Report(exception, operation); ErrorMessage = "カレンダーの読み込みに失敗しました。再試行してください。"; }
}
