using WorkoutTracker.Application;
using WorkoutTracker.App.Services;
using WorkoutTracker.App.ViewModels;
using WorkoutTracker.App.Views;
using System.Windows;
using System.Windows.Controls;

namespace WorkoutTracker.App.Tests;

public sealed class HistoryCalendarViewModelTests
{
    [Fact(DisplayName = "HIS-007 and HIS-008 default Page Size options and change reset keyset Cursor")]
    public async Task HistoryPageSizeAndCursorReset()
    {
        var query = new FakeQuery(); var vm = CreateHistory(query); await vm.InitializeAsync();
        Assert.Equal(50, vm.PageSize); Assert.Equal([25, 50, 100], vm.PageSizeOptions); Assert.Equal(1, vm.PageNumber); Assert.True(vm.CanNextPage);
        await vm.NextPageAsync(); Assert.Equal(2, vm.PageNumber); Assert.NotNull(vm.CurrentCursor);
        vm.PageSize = 25; Assert.Equal(1, vm.PageNumber); Assert.Null(vm.CurrentCursor); Assert.False(vm.CanPreviousPage); Assert.False(vm.IsDrawerOpen);
        await vm.ApplyFiltersAsync(); Assert.Equal(25, query.HistoryRequests[^1].PageSize); Assert.Null(query.HistoryRequests[^1].Cursor);
    }

    [Fact(DisplayName = "HIS-009 through HIS-012 Filter change resets Cursor Drawer and selection before filtered Query")]
    public async Task HistoryFilterReset()
    {
        var query = new FakeQuery(); var vm = CreateHistory(query); await vm.InitializeAsync(); await vm.NextPageAsync(); var item = vm.Items.Single(); await vm.OpenDetailAsync(item); Assert.True(vm.IsDrawerOpen);
        vm.SelectedExercise = vm.ExerciseOptions.Single(); Assert.Null(vm.CurrentCursor); Assert.False(vm.IsDrawerOpen); Assert.Null(vm.SelectedItem); Assert.Equal(1, vm.PageNumber);
        await vm.ApplyFiltersAsync(); var request = query.HistoryRequests[^1]; Assert.Equal(vm.SelectedExercise.Id, request.ExerciseId); Assert.Null(request.Cursor);
    }

    [Fact(DisplayName = "History Drawer open close keeps Filter current Page items selection and list context")]
    public async Task DrawerContext()
    {
        var query = new FakeQuery(); var vm = CreateHistory(query); await vm.InitializeAsync(); var before = vm.Items.ToList(); var selected = before[0]; await vm.OpenDetailAsync(selected);
        Assert.True(vm.IsDrawerOpen); Assert.Equal(selected.Id, vm.SelectedDetail!.WorkoutSessionId); vm.CloseDetail();
        Assert.False(vm.IsDrawerOpen); Assert.Same(selected, vm.SelectedItem); Assert.Equal(before, vm.Items); Assert.Equal(1, vm.PageNumber); Assert.Null(vm.SelectedExercise);
    }

    [Fact(DisplayName = "CAL-003 day selection lists every Workout and empty day has explicit Empty State")]
    public async Task CalendarDaySelection()
    {
        var query = new FakeQuery(); var vm = CreateCalendar(query); await vm.InitializeAsync(); var workoutDay = vm.Cells.Single(x => x.Date?.Day == 10); vm.SelectDay(workoutDay);
        Assert.Equal(2, vm.SelectedWorkouts.Count); Assert.False(vm.IsSelectedDayEmpty); var empty = vm.Cells.First(x => x.Date.HasValue && x.Date.Value.Day != 10); vm.SelectDay(empty); Assert.True(vm.IsSelectedDayEmpty); Assert.Empty(vm.SelectedWorkouts);
    }

    [Fact(DisplayName = "CAL-004 Detail Drawer close preserves Calendar month selected day and Workout list context")]
    public async Task CalendarDetailContext()
    {
        var query = new FakeQuery(); var vm = CreateCalendar(query); await vm.InitializeAsync(); var month = vm.CurrentMonth; var day = vm.Cells.Single(x => x.Date?.Day == 10); vm.SelectDay(day); var workout = vm.SelectedWorkouts[0]; await vm.OpenDetailAsync(workout);
        Assert.True(vm.IsDrawerOpen); Assert.Equal(workout.WorkoutSessionId, vm.SelectedDetail!.WorkoutSessionId); vm.CloseDetailCommand.Execute(null);
        Assert.False(vm.IsDrawerOpen); Assert.Equal(month, vm.CurrentMonth); Assert.Same(day, vm.SelectedDay); Assert.Equal(2, vm.SelectedWorkouts.Count);
    }

    [Fact(DisplayName = "CAL-001 month movement crosses year and leap-February while replacing only current month cells")]
    public async Task CalendarMonthMovement()
    {
        var query = new FakeQuery(); var vm = CreateCalendar(query); await vm.InitializeAsync(); var offset = (2024 - vm.CurrentMonth.Year) * 12 + 2 - vm.CurrentMonth.Month; await vm.ChangeMonthAsync(offset);
        Assert.Equal(new DateOnly(2024, 2, 1), vm.CurrentMonth); Assert.Equal(29, vm.Cells.Count(x => x.Date.HasValue)); Assert.Null(vm.SelectedDay); Assert.Null(vm.SelectedDetail);
        await vm.ChangeMonthAsync(-2); Assert.Equal(new DateOnly(2023, 12, 1), vm.CurrentMonth);
    }

    [Fact(DisplayName = "ASY-001 and ASY-002 Calendar month change cancels prior Query and stale result cannot overwrite latest month")]
    public async Task CalendarCancellationAndLatestResult()
    {
        var query = new DelayedCalendarQuery(); var vm = new CalendarViewModel(new(query, new InlineExecutor())); await vm.InitializeAsync();
        var first = vm.ChangeMonthAsync(1); var firstRequest = query.Pending[^1]; var second = vm.ChangeMonthAsync(1); var secondRequest = query.Pending[^1];
        Assert.True(firstRequest.Token.IsCancellationRequested); secondRequest.Completion.SetResult(DelayedCalendarQuery.Month(secondRequest.Year, secondRequest.Month)); await second;
        var expected = new DateOnly(secondRequest.Year, secondRequest.Month, 1); Assert.Equal(expected, vm.CurrentMonth);
        firstRequest.Completion.SetResult(DelayedCalendarQuery.Month(firstRequest.Year, firstRequest.Month)); await first; Assert.Equal(expected, vm.CurrentMonth); Assert.All(vm.Cells.Where(x => x.Date.HasValue), x => Assert.Equal(expected.Month, x.Date!.Value.Month));
    }

    [Fact(DisplayName = "Human Acceptance History internal exception is hidden and Retry reruns failed filter initialization")]
    public async Task HistoryErrorStateDoesNotExposeInternalExceptionAndCanRetry()
    {
        var query = new FailOnceHistoryQuery(); var reporter = new RecordingExceptionReporter();
        var vm = new HistoryViewModel(new(query, new InlineExecutor()), reporter);

        await vm.InitializeAsync();

        Assert.Equal("履歴の読み込みに失敗しました。再試行してください。", vm.ErrorMessage);
        Assert.DoesNotContain("LINQ", vm.ErrorMessage); Assert.DoesNotContain("http", vm.ErrorMessage);
        var reported = Assert.Single(reporter.Errors); Assert.Contains("LINQ", reported.Exception.Message); Assert.Equal("History.Initialize", reported.Operation);

        await vm.RetryAsync();

        Assert.Null(vm.ErrorMessage); Assert.Single(vm.ExerciseOptions); Assert.Single(vm.Items); Assert.Equal(2, query.FilterCalls);
    }

    [Fact(DisplayName = "Human Acceptance Calendar month Empty State is outside the date-cell Grid")]
    public async Task CalendarMonthEmptyStateDoesNotOverlayDateCells()
    {
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                WpfTestApplication.Run(_ =>
                {
                    var view = new CalendarView();
                    var empty = Assert.IsType<TextBlock>(view.FindName("MonthEmptyState"));
                    var calendar = Assert.IsType<Grid>(view.FindName("CalendarGrid"));
                    Assert.NotEqual(Grid.GetRow(calendar), Grid.GetRow(empty));
                    Assert.Equal(2, Grid.GetRow(empty)); Assert.Equal(3, Grid.GetRow(calendar));
                });
                result.SetResult(null);
            }
            catch (Exception ex) { result.SetResult(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.Null(await result.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact(DisplayName = "Human Acceptance completed navigation has no Placeholder page type")]
    public void NavigationHasNoPlaceholderPageType() =>
        Assert.DoesNotContain(typeof(ShellViewModel).Assembly.GetTypes(), type => type.Name.Contains("Placeholder", StringComparison.OrdinalIgnoreCase));

    private static HistoryViewModel CreateHistory(FakeQuery query) => new(new(query, new InlineExecutor()));
    private static CalendarViewModel CreateCalendar(FakeQuery query) => new(new(query, new InlineExecutor()));
    private sealed class InlineExecutor : IOffUiExecutor
    {
        public Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
        public Task RunAsync(Func<CancellationToken, Task> operation, CancellationToken cancellationToken = default) => operation(cancellationToken);
    }
    private sealed class FakeQuery : IHistoryCalendarQuery
    {
        private static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000003");
        private static readonly Guid SecondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        private static readonly Guid ThirdId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        private static readonly DateOnly Date = new(2026, 8, 10);
        private static readonly DateTimeOffset Completed = new(2026, 8, 10, 3, 0, 0, TimeSpan.Zero);
        public List<HistoryQuery> HistoryRequests { get; } = [];
        public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken cancellationToken = default)
        {
            HistoryRequests.Add(query); if (query.Cursor is null) return Task.FromResult(new HistoryPage([Item(FirstId), Item(SecondId)], new(Date, Completed, SecondId))); return Task.FromResult(new HistoryPage([Item(ThirdId)], null));
        }
        public Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryFilterOptions([new(FirstId, "Bench")], [new(SecondId, "Chest")], [new(ThirdId, "Push")]));
        public Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<WorkoutDetailDto?>(Detail(sessionId));
        public Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken cancellationToken = default)
        {
            var start = new DateOnly(year, month, 1); var days = Enumerable.Range(0, start.AddMonths(1).DayNumber - start.DayNumber).Select(i => { var date = start.AddDays(i); IReadOnlyList<CalendarWorkoutSummaryDto> workouts = date.Day == 10 ? [new(FirstId, "A", "Menu", ["Chest"], 500m, 1), new(SecondId, "B", null, ["Back"], 600m, 2)] : []; return new CalendarDayDto(date, workouts); }).ToList(); return Task.FromResult(new CalendarMonthDto(year, month, days));
        }
        private static WorkoutHistoryItem Item(Guid id) => new(id, Date, Completed, "Workout", null, null, 500m, 1, 1);
        private static WorkoutDetailDto Detail(Guid id) { var set = new WorkoutDetailSetDto(Guid.NewGuid(), 1, 50m, 10, 8m); var exercise = new WorkoutDetailExerciseDto(Guid.NewGuid(), Guid.NewGuid(), "Bench", Guid.NewGuid(), "Chest", 0, null, [set]); return new(id, Date, Completed, "Workout", null, null, [exercise]); }
    }
    private sealed class DelayedCalendarQuery : IHistoryCalendarQuery
    {
        private int _calendarCalls;
        public List<PendingRequest> Pending { get; } = [];
        public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryPage([], null));
        public Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new HistoryFilterOptions([], [], []));
        public Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<WorkoutDetailDto?>(null);
        public Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken cancellationToken = default)
        {
            if (_calendarCalls++ == 0) return Task.FromResult(Month(year, month)); var pending = new PendingRequest(year, month, cancellationToken, new(TaskCreationOptions.RunContinuationsAsynchronously)); Pending.Add(pending); return pending.Completion.Task;
        }
        public static CalendarMonthDto Month(int year, int month) { var start = new DateOnly(year, month, 1); return new(year, month, Enumerable.Range(0, start.AddMonths(1).DayNumber - start.DayNumber).Select(i => new CalendarDayDto(start.AddDays(i), [])).ToList()); }
        public sealed record PendingRequest(int Year, int Month, CancellationToken Token, TaskCompletionSource<CalendarMonthDto> Completion);
    }

    private sealed class FailOnceHistoryQuery : IHistoryCalendarQuery
    {
        private static readonly Guid Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        public int FilterCalls { get; private set; }
        public Task<HistoryFilterOptions> GetHistoryFilterOptionsAsync(CancellationToken cancellationToken = default)
        {
            if (++FilterCalls == 1) throw new InvalidOperationException("The LINQ expression could not be translated. https://internal.example");
            return Task.FromResult(new HistoryFilterOptions([new(Id, "Bench")], [new(Id, "Chest")], [new(Id, "Push")]));
        }
        public Task<HistoryPage> GetHistoryPageAsync(HistoryQuery query, CancellationToken cancellationToken = default) => Task.FromResult(new HistoryPage([new(Id, new DateOnly(2026, 8, 22), new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero), "Workout", null, null, 500m, 1, 1)], null));
        public Task<WorkoutDetailDto?> GetWorkoutDetailAsync(Guid sessionId, CancellationToken cancellationToken = default) => Task.FromResult<WorkoutDetailDto?>(null);
        public Task<CalendarMonthDto> GetCalendarMonthAsync(int year, int month, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class RecordingExceptionReporter : IUiExceptionReporter
    {
        public List<(Exception Exception, string Operation)> Errors { get; } = [];
        public void Report(Exception exception, string operation) => Errors.Add((exception, operation));
    }
}
