using System.Windows.Input;
using WorkoutTracker.Application;

namespace WorkoutTracker.App.ViewModels;

public sealed class ShellViewModel : ObservableObject
{
    private readonly ActiveWorkoutViewModel _workout;
    private readonly MasterDataViewModel? _masterData;
    private readonly HistoryViewModel? _history;
    private readonly CalendarViewModel? _calendar;
    private readonly DashboardViewModel? _dashboard;
    private readonly StatisticsViewModel? _statistics;
    private readonly SettingsViewModel? _settings;
    private readonly IWriteAdmissionGate? _writeGate;
    private object _currentPage;
    private bool _initialized;
    public ShellViewModel(ActiveWorkoutViewModel workout, MasterDataViewModel? masterData = null, HistoryViewModel? history = null, CalendarViewModel? calendar = null, DashboardViewModel? dashboard = null, StatisticsViewModel? statistics = null, SettingsViewModel? settings = null, IWriteAdmissionGate? writeGate = null)
    {
        _workout = workout; _masterData = masterData; _history = history; _calendar = calendar; _dashboard = dashboard; _statistics = statistics; _settings = settings; _writeGate = writeGate; _currentPage = (object?)dashboard ?? workout;
        if (_writeGate is not null) _writeGate.Changed += (_, _) => { OnPropertyChanged(nameof(IsMaintenanceActive)); OnPropertyChanged(nameof(MaintenanceMessage)); OnPropertyChanged(nameof(MaintenanceProgress)); };
        if (_masterData is not null) _masterData.WorkoutStarted += (_, _) => CurrentPage = _workout;
        if (_dashboard is not null) _dashboard.StartWorkoutRequested += (_, _) => CurrentPage = _workout;
        _workout.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(ActiveWorkoutViewModel.IsActive)) OnPropertyChanged(nameof(HasActiveWorkout)); };
        NavigateDashboardCommand = new AsyncRelayCommand(async () => { if (_dashboard is not null) { CurrentPage = _dashboard; await _dashboard.InitializeAsync(); } });
        NavigateWorkoutCommand = new RelayCommand(() => CurrentPage = _workout);
        NavigateHistoryCommand = new AsyncRelayCommand(async () => { if (_history is not null) { CurrentPage = _history; await _history.InitializeAsync(); } });
        NavigateMenuCommand = new AsyncRelayCommand(async () => { if (_masterData is not null) { CurrentPage = _masterData; await _masterData.InitializeAsync(); } });
        NavigateStatisticsCommand = new AsyncRelayCommand(async () => { if (_statistics is not null) { CurrentPage = _statistics; await _statistics.InitializeAsync(); } });
        NavigateCalendarCommand = new AsyncRelayCommand(async () => { if (_calendar is not null) { CurrentPage = _calendar; await _calendar.InitializeAsync(); } });
        NavigateSettingsCommand = new AsyncRelayCommand(async () => { if (_settings is not null) { CurrentPage = _settings; await _settings.InitializeAsync(); } });
    }
    public object CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value)) OnPropertyChanged(nameof(CurrentPageKey));
        }
    }
    public string CurrentPageKey => CurrentPage switch
    {
        DashboardViewModel => "Dashboard",
        ActiveWorkoutViewModel => "Workout",
        HistoryViewModel => "History",
        MasterDataViewModel => "MasterData",
        StatisticsViewModel => "Statistics",
        CalendarViewModel => "Calendar",
        SettingsViewModel => "Settings",
        _ => string.Empty
    };
    public bool HasActiveWorkout => _workout.IsActive;
    public bool IsMaintenanceActive => _writeGate?.Status.IsActive == true;
    public string MaintenanceMessage => _writeGate?.Status.Message ?? string.Empty;
    public int MaintenanceProgress => _writeGate?.Status.ProgressPercent ?? 0;
    public ICommand NavigateDashboardCommand { get; }
    public ICommand NavigateWorkoutCommand { get; }
    public ICommand NavigateHistoryCommand { get; }
    public ICommand NavigateMenuCommand { get; }
    public ICommand NavigateStatisticsCommand { get; }
    public ICommand NavigateCalendarCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public async Task InitializeAsync() { if (_initialized) return; await _workout.InitializeAsync(); if (_dashboard is not null) await _dashboard.InitializeAsync(); _initialized = true; OnPropertyChanged(nameof(HasActiveWorkout)); }
    public void ContinueActiveWorkout() => CurrentPage = _workout;
    public Task<bool> DiscardActiveWorkoutAsync() => _workout.DiscardAsync(true);
}
