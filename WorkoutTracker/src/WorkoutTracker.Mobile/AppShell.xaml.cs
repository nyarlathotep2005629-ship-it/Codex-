namespace WorkoutTracker.Mobile;

public partial class AppShell : Shell
{
    public AppShell(MobileWorkoutController workouts)
    {
        InitializeComponent();
        Items.Add(new TabBar
        {
            Items =
            {
                Tab("Home", "home", new HomePage(workouts)),
                Tab("Workout", "workout", new WorkoutPage(workouts)),
                Tab("History", "history", new HistoryPage(workouts)),
                Tab("Statistics", "statistics", new StatisticsPage(workouts)),
                Tab("More", "more", new MorePage(workouts))
            }
        });
    }

    private static ShellContent Tab(string title, string route, Page page) => new()
    {
        Title = title,
        Route = route,
        Content = page
    };
}
