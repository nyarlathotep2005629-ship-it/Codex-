using Microsoft.Extensions.DependencyInjection;

namespace WorkoutTracker.Mobile;

public partial class App : Microsoft.Maui.Controls.Application
{
	private readonly MobileWorkoutController _workouts;

	public App(MobileWorkoutController workouts)
	{
		_workouts = workouts;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var window = new Window(new AppShell(_workouts));
		window.Stopped += async (_, _) => await _workouts.FlushForBackgroundAsync();
		return window;
	}
}
