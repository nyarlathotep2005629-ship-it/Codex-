using System.Windows.Threading;
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Views;
using WorkoutTracker.Application;
using WorkoutTracker.Infrastructure;

namespace WorkoutTracker.App.Tests;

public sealed class WorkoutViewModelTests
{
    [Fact(DisplayName = "ARC-001 Workout ViewModel composition has no captive scoped dependency")]
    public void Arc001Composition()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WorkoutTracker.App.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var services = new ServiceCollection(); services.AddApplication(); services.AddInfrastructure(Path.Combine(directory, "test.db")); services.AddSingleton<ActiveWorkoutViewModel>(); services.AddSingleton<ShellViewModel>();
            using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }); Assert.NotNull(provider.GetRequiredService<ShellViewModel>());
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    [Fact(DisplayName = "APP-018 Suggestion properties are separate and absent from SaveDraft payload")]
    public void App018()
    {
        var dtoProperties = typeof(ActiveWorkoutSetDto).GetProperties().Select(x => x.Name).ToArray();
        var saveProperties = typeof(SaveDraftSetRequest).GetProperties().Select(x => x.Name).ToArray();
        Assert.Contains("SuggestedWeightKg", dtoProperties); Assert.DoesNotContain("SuggestedWeightKg", saveProperties); Assert.DoesNotContain("SuggestedReps", saveProperties);
    }

    [Fact(DisplayName = "APP-019 suggestion Enter copies actual value once and requests next Focus")]
    public void App019()
    {
        var changes = new List<bool>(); WorkoutFocusField? next = null;
        var set = new WorkoutSetViewModel(new(Guid.NewGuid(), 1, null, null, null, 100m, 8), (immediate, _) => changes.Add(immediate), (_, field) => next = field);
        set.HandleEnter(WorkoutFocusField.Weight);
        Assert.Equal(100m, set.WeightKg); Assert.Equal([true], changes); Assert.Equal(WorkoutFocusField.Weight, next); Assert.False(set.HasWeightSuggestion);
    }

    [Fact(DisplayName = "APP-020 Tab does not accept a visible Suggestion")]
    public void App020()
    {
        var revisions = 0; var set = new WorkoutSetViewModel(new(Guid.NewGuid(), 1, null, null, null, 100m, 8), (_, _) => revisions++, (_, _) => { });
        Assert.True(set.HasWeightSuggestion); Assert.Null(set.WeightKg); Assert.Equal(0, revisions);
    }

    [Fact(DisplayName = "APP-021 Enter without Suggestion keeps actual value null and moves Focus")]
    public void App021()
    {
        var changes = 0; WorkoutFocusField? moved = null; var set = new WorkoutSetViewModel(new(Guid.NewGuid(), 1, null, null, null, null, null), (_, _) => changes++, (_, field) => moved = field);
        set.HandleEnter(WorkoutFocusField.Weight); Assert.Null(set.WeightKg); Assert.Equal(0, changes); Assert.Equal(WorkoutFocusField.Weight, moved);
    }

    [Fact(DisplayName = "UI-001 Tab and Enter field order is Weight Reps RPE next Set Weight")]
    public void Ui001()
    {
        Assert.Equal(new(0, WorkoutFocusField.Reps), WorkoutFocusPlanner.Next(WorkoutFocusField.Weight, 0, 2));
        Assert.Equal(new(0, WorkoutFocusField.Rpe), WorkoutFocusPlanner.Next(WorkoutFocusField.Reps, 0, 2));
        Assert.Equal(new(1, WorkoutFocusField.Weight), WorkoutFocusPlanner.Next(WorkoutFocusField.Rpe, 0, 2));
    }

    [Fact(DisplayName = "UI-002 final RPE Enter targets Add Set without adding a Set")]
    public void Ui002() => Assert.Equal(new(null, WorkoutFocusField.AddSet), WorkoutFocusPlanner.Next(WorkoutFocusField.Rpe, 2, 3));

    [Fact(DisplayName = "UI-004 and UI-005 Focus waits at DispatcherPriority.Loaded")]
    public void Ui004Ui005() => Assert.Equal(DispatcherPriority.Loaded, WorkoutView.FocusDispatcherPriority);

    [Fact(DisplayName = "UI-003 and UI-008 Set add appends a contiguous Set and requests its Weight Focus")]
    public async Task Ui003Ui008()
    {
        var h = new VmHarness(); await h.ViewModel.InitializeAsync(); WorkoutFocusRequest? request = null; h.ViewModel.FocusRequested += (_, value) => request = value;
        var exercise = h.ViewModel.Exercises.Single(); h.ViewModel.AddSet(exercise);
        Assert.Equal(2, exercise.Sets.Count); Assert.Equal([1, 2], exercise.Sets.Select(x => x.SetNumber)); Assert.Equal(exercise.Sets[1].WorkoutSetId, request!.WorkoutSetId); Assert.Equal(WorkoutFocusField.Weight, request.Field);
    }

    [Fact(DisplayName = "UI-011 stale Focus request can be invalidated before Dispatcher resumes")]
    public async Task Ui011()
    {
        var h = new VmHarness(); await h.ViewModel.InitializeAsync(); WorkoutFocusRequest? request = null; h.ViewModel.FocusRequested += (_, value) => request = value;
        h.ViewModel.AddSet(h.ViewModel.Exercises.Single()); Assert.True(h.ViewModel.IsCurrentFocusRequest(request!.RequestId));
        h.ViewModel.InvalidateFocusRequest(); Assert.False(h.ViewModel.IsCurrentFocusRequest(request.RequestId));
    }

    [Fact(DisplayName = "ACT-001 ACT-002 UI-006 and UI-007 navigation reuses in-memory Active Workout")]
    public async Task Act001Act002Ui006Ui007()
    {
        var h = new VmHarness(); var executor = new InlineExecutor(); var historyQuery = new EmptyHistoryQuery(); var history = new HistoryViewModel(new(historyQuery, executor)); var calendar = new CalendarViewModel(new(historyQuery, executor)); await calendar.InitializeAsync(); var analytics = new AnalyticsService(new EmptyAnalyticsQuery(), executor); var dashboard = new DashboardViewModel(analytics); var statistics = new StatisticsViewModel(analytics); var shell = new ShellViewModel(h.ViewModel, null, history, calendar, dashboard, statistics); await shell.InitializeAsync();
        var session = h.ViewModel.SessionId; h.ViewModel.Exercises[0].Sets[0].WeightKg = 100m; var revision = h.State.EditRevision;
        shell.NavigateDashboardCommand.Execute(null); Assert.Same(dashboard, shell.CurrentPage); shell.NavigateWorkoutCommand.Execute(null);
        Assert.Same(h.ViewModel, shell.CurrentPage); Assert.Equal(session, h.ViewModel.SessionId); Assert.Equal(100m, h.ViewModel.Exercises[0].Sets[0].WeightKg); Assert.Equal(revision, h.State.EditRevision); Assert.Equal(0, h.Persistence.LoadCount);
        shell.NavigateHistoryCommand.Execute(null); Assert.Same(history, shell.CurrentPage); shell.NavigateWorkoutCommand.Execute(null); Assert.Same(h.ViewModel, shell.CurrentPage); Assert.Equal(session, h.ViewModel.SessionId); Assert.Equal(revision, h.State.EditRevision);
        shell.NavigateCalendarCommand.Execute(null); Assert.Same(calendar, shell.CurrentPage); shell.NavigateWorkoutCommand.Execute(null); Assert.Same(h.ViewModel, shell.CurrentPage); Assert.Equal(session, h.ViewModel.SessionId); Assert.Equal(revision, h.State.EditRevision);
        var beforeUnavailableMenu = shell.CurrentPage; shell.NavigateMenuCommand.Execute(null); Assert.Same(beforeUnavailableMenu, shell.CurrentPage);
        shell.NavigateStatisticsCommand.Execute(null); Assert.Same(statistics, shell.CurrentPage); shell.NavigateWorkoutCommand.Execute(null); Assert.Equal(session, h.ViewModel.SessionId); Assert.Equal(revision, h.State.EditRevision);
    }

    [Fact(DisplayName = "APP-037 Discard confirmation cancel preserves Draft and Active State")]
    public async Task App037()
    {
        var h = new VmHarness(); await h.ViewModel.InitializeAsync(); var session = h.ViewModel.SessionId;
        Assert.False(await h.ViewModel.DiscardAsync(false)); Assert.Equal(session, h.ViewModel.SessionId); Assert.Equal(0, h.Persistence.DiscardCount);
    }

    [Fact(DisplayName = "VAL-010 Active Exercise keeps Set count within 1 through 100")]
    public async Task Val010()
    {
        var h = new VmHarness(); await h.ViewModel.InitializeAsync(); var exercise = h.ViewModel.Exercises.Single();
        while (exercise.Sets.Count < 100) h.ViewModel.AddSet(exercise);
        h.ViewModel.AddSet(exercise); Assert.Equal(100, exercise.Sets.Count); Assert.Contains("100", h.ViewModel.LastOperationError);
        while (exercise.Sets.Count > 1) h.ViewModel.RemoveSet(exercise, exercise.Sets[^1]);
        h.ViewModel.RemoveSet(exercise, exercise.Sets[0]); Assert.Single(exercise.Sets);
    }

    [Fact(DisplayName = "VAL-016 VAL-017 and VAL-019 invalid input retains a textual error and blocks persistence")]
    public async Task Val016Val017Val019()
    {
        var h = new VmHarness(); await h.ViewModel.InitializeAsync(); var set = h.ViewModel.Exercises[0].Sets[0];
        set.WeightKg = 0m; Assert.True(set.HasErrors); Assert.NotNull(set.WeightError);
        h.ViewModel.WorkoutNote = new string('x', 4001); Assert.Contains("4000", h.ViewModel.LastOperationError);
        h.ViewModel.WorkoutDate = new DateOnly(1899, 12, 31); Assert.Contains("1900", h.ViewModel.LastOperationError);
        Assert.Equal(0, h.Persistence.SaveCount);
    }

    private sealed class VmHarness
    {
        public VmHarness()
        {
            var set = new ActiveWorkoutSetDto(Guid.NewGuid(), 1, null, null, null, 100m, 8);
            var exercise = new ActiveWorkoutExerciseDto(Guid.NewGuid(), Guid.NewGuid(), "Bench", Guid.NewGuid(), "Chest", 0, null, null, [set]);
            var dto = new ActiveWorkoutDto(Guid.NewGuid(), null, "Workout", new DateOnly(2026, 8, 22), null, [exercise]);
            State.SetStarted(dto); Persistence = new(dto); var executor = new InlineExecutor(); var clock = new FixedClock();
            var delay = new NeverDelay(); var autoSave = new AutoSaveCoordinator(Persistence, State, clock, delay);
            ViewModel = new(new(Persistence, State, executor), new(Persistence, State, clock, executor), new(Persistence, executor), new(Persistence, State, autoSave, clock, executor), new(Persistence, State, autoSave, executor), autoSave, State, new EmptyExerciseRepository(), new EmptyMenuRepository(), executor);
        }
        public ActiveWorkoutState State { get; } = new();
        public FakePersistence Persistence { get; }
        public ActiveWorkoutViewModel ViewModel { get; }
    }

    private sealed class InlineExecutor : IOffUiExecutor
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class FixedClock : IClock { public DateTimeOffset UtcNow => new(2026, 8, 22, 3, 0, 0, TimeSpan.Zero); }
    private sealed class NeverDelay : IAutoSaveDelay { public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
    private sealed class EmptyExerciseRepository : IExerciseRepository
    {
        public Task<IReadOnlyList<ExerciseSummary>> GetActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExerciseSummary>>([]);
        public Task<WorkoutTracker.Domain.Exercise?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<WorkoutTracker.Domain.Exercise?>(null);
        public Task<IReadOnlyList<ExerciseSummary>> SearchAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExerciseSummary>>([]);
    }
    private sealed class EmptyMenuRepository : IWorkoutMenuRepository
    {
        public Task<IReadOnlyList<WorkoutMenuSummary>> GetSummariesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<WorkoutMenuSummary>>([]);
        public Task<WorkoutTracker.Domain.WorkoutMenu?> GetWithExercisesAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult<WorkoutTracker.Domain.WorkoutMenu?>(null);
    }
    private sealed class EmptyHistoryQuery : IHistoryCalendarQuery
    {
        public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryPage([], null));
        public Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryFilterOptions([], [], []));
        public Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<WorkoutDetailDto?>(null);
        public Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken cancellationToken = default) { var start = new DateOnly(year, month, 1); return Task.FromResult(new CalendarMonthDto(year, month, Enumerable.Range(0, start.AddMonths(1).DayNumber - start.DayNumber).Select(i => new CalendarDayDto(start.AddDays(i), [])).ToList())); }
    }
    private sealed class EmptyAnalyticsQuery : IAnalyticsQuery
    {
        public Task<DateOnly?> GetOldestCompletedWorkoutDateAsync(CancellationToken cancellationToken = default) => Task.FromResult<DateOnly?>(null);
        public Task<IReadOnlyList<ExerciseOptionDto>> GetExerciseOptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ExerciseOptionDto>>([]);
        public Task<AnalyticsSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new AnalyticsSettings(WorkoutTracker.Domain.DashboardPeriod.ThreeMonths, []));
        public Task SaveDashboardSettingsAsync(AnalyticsSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<DashboardDto> GetDashboardAsync(DashboardReadRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new DashboardDto(request.Period, new DashboardSummaryDto(request.SelectedPeriod, 0, 0m, 0, []), [], [], [], [], [], []));
        public Task<StatisticsDto> GetStatisticsAsync(StatisticsReadRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FakePersistence(ActiveWorkoutDto dto) : IWorkoutVerticalSlicePersistence
    {
        public int LoadCount { get; private set; }
        public int DiscardCount { get; private set; }
        public int SaveCount { get; private set; }
        public Task<StartPersistenceResult> StartAsync(StartWorkoutRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult(new StartPersistenceResult(false, dto, null));
        public Task<ActiveWorkoutDto?> LoadDraftAsync(CancellationToken cancellationToken = default) { LoadCount++; return Task.FromResult<ActiveWorkoutDto?>(dto); }
        public Task<PreviousPerformanceDto?> GetLatestCompletedPerformanceAsync(Guid exerciseId, CancellationToken cancellationToken = default) => Task.FromResult<PreviousPerformanceDto?>(null);
        public Task SaveDraftAsync(SaveDraftRequest request, DateTimeOffset now, CancellationToken cancellationToken = default) { SaveCount++; return Task.CompletedTask; }
        public Task<FinishWorkoutResult> FinishAsync(Guid sessionId, DateTimeOffset now, CancellationToken cancellationToken = default) => Task.FromResult(FinishWorkoutResult.Success());
        public Task DiscardAsync(Guid sessionId, CancellationToken cancellationToken = default) { DiscardCount++; return Task.CompletedTask; }
    }
}
