# Android development

## Local build entry

The repository remains at `C:\Users\hiroki\Documents\Workout Tracker_筋トレ結果集積アプリ`.
Android `aapt2` packaging cannot reliably consume this Japanese-containing path and reports `APT2265`.
Use the verified ASCII junction below for every Android restore, build, package, install, and run command:

```powershell
C:\WorkoutTrackerMobileSpike
```

Before Android work, confirm that this is a **Junction** whose `ResolvedTarget` is the repository above. Do not copy source files into the junction and do not relocate the repository. Windows WPF builds run from the original repository path.

## Android product database

`WorkoutTracker.Mobile` uses only the Android app-private path:

```text
FileSystem.AppDataDirectory\workout-tracker.db
```

It is intentionally separate from `m0-technical-spike.db` and from the Windows user database. Startup applies the existing EF migration, validates the local database through the existing startup coordinator, then detects and rehydrates a committed Draft.

## Composition and durability

MAUI startup owns the app-private path, dependency-injection composition, lifecycle hook, and presentation. It reuses `WorkoutTracker.Domain`, `WorkoutTracker.Application`, and `WorkoutTracker.Infrastructure`; pages do not create or use `DbContext` directly.

Draft edits use the shared `AutoSaveCoordinator` (500 ms debounce and revision-based single-flight). Backgrounding requests a best-effort flush only. A successfully committed SQLite Draft is the source of truth; lifecycle callbacks are not a durability guarantee.

## M1 navigation scope

The five primary tabs remain Home, Workout, History, Statistics, and More. M1 implements the local Home → Workout → autosave/rehydrate → Finish → History path. Statistics and More remain intentionally minimal. Sync, LAN, `.wtb`, backup UI, and master-data authoring are outside this scope.
