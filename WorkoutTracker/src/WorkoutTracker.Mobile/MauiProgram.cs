using Microsoft.Extensions.Logging;

using WorkoutTracker.Application;
using WorkoutTracker.Infrastructure;

namespace WorkoutTracker.Mobile;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		var databasePath = MobileDatabaseLocation.GetProductDatabasePath();
		builder.Services.AddApplication();
		builder.Services.AddInfrastructure(databasePath);
		builder.Services.AddSingleton<ITransferFileSaveService, AndroidTransferFileSaveService>();
		builder.Services.AddSingleton<MobileWorkoutController>();

		return builder.Build();
	}
}
