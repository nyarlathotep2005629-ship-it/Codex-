namespace WorkoutTracker.Mobile;

/// <summary>Android-local database location, separate from the M0 spike database.</summary>
public static class MobileDatabaseLocation
{
    public const string ProductDatabaseFileName = "workout-tracker.db";

    public static string GetProductDatabasePath()
    {
        Directory.CreateDirectory(FileSystem.AppDataDirectory);
        return Path.Combine(FileSystem.AppDataDirectory, ProductDatabaseFileName);
    }
}
