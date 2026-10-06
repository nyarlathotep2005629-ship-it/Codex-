using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using WorkoutTracker.Application;
using WorkoutTracker.App.Services;
using WorkoutTracker.Domain;

namespace WorkoutTracker.App.ViewModels;

public sealed class DashboardExerciseOptionViewModel : ObservableObject
{
    private bool _isSelected;
    public DashboardExerciseOptionViewModel(ExerciseOptionDto option, bool selected) { Option = option; _isSelected = selected; }
    public ExerciseOptionDto Option { get; }
    public Guid Id => Option.Id;
    public string Name => Option.Name;
    public bool IsSelected { get => _isSelected; set { if (SetProperty(ref _isSelected, value)) SelectionChanged?.Invoke(this, EventArgs.Empty); } }
    public event EventHandler? SelectionChanged;
}

public sealed class DashboardViewModel : ObservableObject
{
    private readonly AnalyticsService _service;
    private readonly IUiExceptionReporter _errors;
    private CancellationTokenSource? _queryCancellation;
    private long _operationId;
    private DashboardPeriod _selectedPeriod = DashboardPeriod.ThreeMonths;
    private DashboardDto? _data;
    private bool _isLoading;
    private bool _initialized;
    private bool _suppressSelection;
    private string? _errorMessage;

    public DashboardViewModel(AnalyticsService service, IUiExceptionReporter? errors = null)
    {
        _service = service; _errors = errors ?? NullUiExceptionReporter.Instance; RetryCommand = new AsyncRelayCommand(RefreshAsync);
        StartWorkoutCommand = new RelayCommand(() => StartWorkoutRequested?.Invoke(this, EventArgs.Empty));
    }

    public ObservableCollection<DashboardExerciseOptionViewModel> ExerciseOptions { get; } = [];
    public IReadOnlyList<DashboardPeriod> PeriodOptions { get; } = Enum.GetValues<DashboardPeriod>();
    public DashboardPeriod SelectedPeriod { get => _selectedPeriod; set { if (SetProperty(ref _selectedPeriod, value) && _initialized) _ = RefreshAndSaveAsync(); } }
    public DashboardDto? Data { get => _data; private set { if (SetProperty(ref _data, value)) RaiseDataProperties(); } }
    public DashboardSummaryDto? Summary => Data?.Summary;
    public IReadOnlyList<DashboardComparisonDto> Comparisons => Data?.Comparisons ?? [];
    public IReadOnlyList<WorkoutFrequencyPointDto> Frequency => Data?.Frequency ?? [];
    public IReadOnlyList<WorkoutFrequencyPointDto> CompactFrequency => Frequency.Where(x => x.WorkoutCount > 0).TakeLast(8).ToList();
    public int MaxFrequencyWorkoutCount => Math.Max(1, CompactFrequency.Select(x => x.WorkoutCount).DefaultIfEmpty().Max());
    public bool HasFrequencyData => CompactFrequency.Count > 0;
    public IReadOnlyList<VolumeSeriesDto> VolumeByWeek => Data?.VolumeByWeek ?? [];
    public IReadOnlyList<MuscleVolumeDto> MuscleVolumes => Data?.MuscleVolumes ?? [];
    public IReadOnlyList<RecentWorkoutDto> RecentWorkouts => Data?.RecentWorkouts ?? [];
    public IReadOnlyList<ChartSeriesDto> OneRepMaxSeries => Data?.OneRepMaxSeries ?? [];
    public string PeriodLabel => Data is null ? string.Empty : $"{Data.Range.Start:yyyy/MM/dd} – {Data.Range.EndExclusive.AddDays(-1):yyyy/MM/dd}";
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) RaiseState(); } }
    public string? ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) RaiseState(); } }
    public bool IsEmpty => !IsLoading && ErrorMessage is null && (Summary?.WorkoutCount ?? 0) == 0;
    public string PageState => IsLoading ? "Loading" : ErrorMessage is not null ? "Error" : IsEmpty ? "Empty" : "Loaded";
    public ICommand RetryCommand { get; }
    public ICommand StartWorkoutCommand { get; }
    public event EventHandler? StartWorkoutRequested;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true; IsLoading = true;
        try
        {
            var settings = await _service.GetSettingsAsync();
            var options = await _service.GetExerciseOptionsAsync();
            _suppressSelection = true; _selectedPeriod = settings.Period; OnPropertyChanged(nameof(SelectedPeriod));
            foreach (var option in options)
            {
                var item = new DashboardExerciseOptionViewModel(option, settings.DashboardExerciseIds.Contains(option.Id));
                item.SelectionChanged += OnExerciseSelectionChanged; ExerciseOptions.Add(item);
            }
            _suppressSelection = false;
            await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure(ex, "Dashboard.Initialize"); IsLoading = false; }
    }

    public Task RefreshAsync() => LoadAsync(saveSettings: false);
    public async Task ReloadAsync()
    {
        _queryCancellation?.Cancel(); _queryCancellation?.Dispose(); _queryCancellation = null;
        _initialized = false; _suppressSelection = true; ExerciseOptions.Clear(); _suppressSelection = false; Data = null;
        await InitializeAsync();
    }
    private Task RefreshAndSaveAsync() => LoadAsync(saveSettings: true);

    private async void OnExerciseSelectionChanged(object? sender, EventArgs e)
    {
        if (_suppressSelection || sender is not DashboardExerciseOptionViewModel changed) return;
        if (ExerciseOptions.Count(x => x.IsSelected) > 4)
        {
            _suppressSelection = true; changed.IsSelected = false; _suppressSelection = false;
        ErrorMessage = "ダッシュボードでは種目を最大4件まで選択できます。"; return;
        }
        await RefreshAndSaveAsync();
    }

    private async Task LoadAsync(bool saveSettings)
    {
        var operation = Interlocked.Increment(ref _operationId); _queryCancellation?.Cancel(); _queryCancellation?.Dispose(); var cts = _queryCancellation = new();
        IsLoading = true; ErrorMessage = null;
        try
        {
            var selected = ExerciseOptions.Where(x => x.IsSelected).Select(x => x.Id).ToList();
            if (saveSettings) await _service.SaveDashboardSettingsAsync(SelectedPeriod, selected, cts.Token);
            var result = await _service.GetDashboardAsync(SelectedPeriod, DateOnly.FromDateTime(DateTime.Today), selected, cts.Token);
            if (operation == _operationId) Data = result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { if (operation == _operationId) ReportFailure(ex, "Dashboard.Load"); }
        finally { if (operation == _operationId) IsLoading = false; }
    }

    private void RaiseDataProperties()
    {
        OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Comparisons)); OnPropertyChanged(nameof(Frequency)); OnPropertyChanged(nameof(CompactFrequency)); OnPropertyChanged(nameof(MaxFrequencyWorkoutCount)); OnPropertyChanged(nameof(HasFrequencyData)); OnPropertyChanged(nameof(VolumeByWeek));
        OnPropertyChanged(nameof(MuscleVolumes)); OnPropertyChanged(nameof(RecentWorkouts)); OnPropertyChanged(nameof(OneRepMaxSeries)); OnPropertyChanged(nameof(PeriodLabel)); RaiseState();
    }
    private void RaiseState() { OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(PageState)); }
    private void ReportFailure(Exception exception, string operation) { _errors.Report(exception, operation); ErrorMessage = "ダッシュボードの読み込みに失敗しました。再試行してください。"; }
}

public sealed class StatisticsViewModel : ObservableObject
{
    private readonly AnalyticsService _service;
    private readonly IUiExceptionReporter _errors;
    private CancellationTokenSource? _queryCancellation;
    private long _operationId;
    private ExerciseOptionDto? _selectedExercise;
    private DashboardPeriod _selectedPeriod = DashboardPeriod.ThreeMonths;
    private StatisticsMetric _selectedMetric = StatisticsMetric.EstimatedOneRepMax;
    private StatisticsDto? _data;
    private bool _isLoading;
    private bool _initialized;
    private string? _errorMessage;

    public StatisticsViewModel(AnalyticsService service, IUiExceptionReporter? errors = null) { _service = service; _errors = errors ?? NullUiExceptionReporter.Instance; RetryCommand = new AsyncRelayCommand(RefreshAsync); }
    public ObservableCollection<ExerciseOptionDto> ExerciseOptions { get; } = [];
    public IReadOnlyList<DashboardPeriod> PeriodOptions { get; } = Enum.GetValues<DashboardPeriod>();
    public IReadOnlyList<StatisticsMetric> MetricOptions { get; } = Enum.GetValues<StatisticsMetric>();
    public ExerciseOptionDto? SelectedExercise { get => _selectedExercise; set { if (SetProperty(ref _selectedExercise, value) && _initialized) _ = RefreshAsync(); } }
    public DashboardPeriod SelectedPeriod { get => _selectedPeriod; set { if (SetProperty(ref _selectedPeriod, value) && _initialized) _ = RefreshAsync(); } }
    public StatisticsMetric SelectedMetric { get => _selectedMetric; set { if (SetProperty(ref _selectedMetric, value) && _initialized) _ = RefreshAsync(); } }
    public StatisticsDto? Data { get => _data; private set { if (SetProperty(ref _data, value)) { OnPropertyChanged(nameof(Series)); OnPropertyChanged(nameof(CurrentOneRepMaxText)); OnPropertyChanged(nameof(BestOneRepMaxText)); OnPropertyChanged(nameof(MaxWeightText)); OnPropertyChanged(nameof(WorkoutCountText)); RaiseState(); } } }
    public ChartSeriesDto? Series => Data?.Series;
    public string CurrentOneRepMaxText => FormatKg(Data?.CurrentOneRepMax);
    public string BestOneRepMaxText => FormatKg(Data?.BestOneRepMax);
    public string MaxWeightText => FormatKg(Data?.MaxWeight);
    public string WorkoutCountText => (Data?.WorkoutCount ?? 0).ToString(CultureInfo.InvariantCulture);
    public bool IsLoading { get => _isLoading; private set { if (SetProperty(ref _isLoading, value)) RaiseState(); } }
    public string? ErrorMessage { get => _errorMessage; private set { if (SetProperty(ref _errorMessage, value)) RaiseState(); } }
    public bool IsEmpty => !IsLoading && ErrorMessage is null && (Series?.Points.Count ?? 0) == 0;
    public string PageState => IsLoading ? "Loading" : ErrorMessage is not null ? "Error" : IsEmpty ? "Empty" : "Loaded";
    public ICommand RetryCommand { get; }

    public async Task InitializeAsync()
    {
        if (_initialized) return; _initialized = true;
        try
        {
            var options = await _service.GetExerciseOptionsAsync(); foreach (var option in options) ExerciseOptions.Add(option);
            _selectedExercise = ExerciseOptions.FirstOrDefault(); OnPropertyChanged(nameof(SelectedExercise)); await RefreshAsync();
        }
        catch (Exception ex) { ReportFailure(ex, "Statistics.Initialize"); IsLoading = false; }
    }

    public async Task RefreshAsync()
    {
        if (SelectedExercise is null) { Data = null; return; }
        var operation = Interlocked.Increment(ref _operationId); _queryCancellation?.Cancel(); _queryCancellation?.Dispose(); var cts = _queryCancellation = new(); IsLoading = true; ErrorMessage = null;
        try
        {
            var result = await _service.GetStatisticsAsync(SelectedExercise.Id, SelectedPeriod, SelectedMetric, DateOnly.FromDateTime(DateTime.Today), cts.Token);
            if (operation == _operationId) Data = result;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) { if (operation == _operationId) ReportFailure(ex, "Statistics.Load"); }
        finally { if (operation == _operationId) IsLoading = false; }
    }
    public async Task ReloadAsync()
    {
        _queryCancellation?.Cancel(); _queryCancellation?.Dispose(); _queryCancellation = null;
        _initialized = false; ExerciseOptions.Clear(); Data = null;
        await InitializeAsync();
    }
    private void RaiseState() { OnPropertyChanged(nameof(IsEmpty)); OnPropertyChanged(nameof(PageState)); }
    private void ReportFailure(Exception exception, string operation) { _errors.Report(exception, operation); ErrorMessage = "統計の読み込みに失敗しました。再試行してください。"; }
    private static string FormatKg(decimal? value) => value.HasValue ? $"{decimal.Round(value.Value, 1, MidpointRounding.AwayFromZero):0.0} kg" : "—";
}
