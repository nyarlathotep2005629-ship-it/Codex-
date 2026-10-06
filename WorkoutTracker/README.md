# Workout Tracker

Workout Tracker is a Windows desktop workout-recording application under continuous development. This directory is a portfolio-oriented snapshot of the current source state; it is not presented as a completed or versioned release.

## Current scope

The repository contains a Windows WPF application, shared Domain/Application/Infrastructure projects, a .NET MAUI Android project, and automated tests. The current code and accompanying documents cover the following areas:

- Workout recording and master data
- Active Draft persistence, Autosave, and Rehydrate
- History, Calendar, Dashboard, and Statistics
- SQLite persistence through EF Core
- Backup/Restore and startup Migration handling
- Keyset-based history pagination
- System, Light, and Dark theme support in the Windows UI
- Local Android presentation work that reuses the shared core
- Domain, Application, Infrastructure, and WPF-oriented automated tests

Manual transfer and multi-device work is still under development. This snapshot does not claim that every planned device, transfer, or human-acceptance gate is complete.

## Technology

- C# / .NET 10
- WPF and XAML for the Windows desktop application
- .NET MAUI / Android for the mobile project
- MVVM-oriented WPF presentation structure
- SQLite and Entity Framework Core 10
- ScottPlot.WPF for Windows charts
- xUnit v3 and Microsoft.NET.Test.Sdk for automated tests

Package versions are centrally managed in `Directory.Packages.props`. The local `dotnet-ef` tool version is pinned in `.config/dotnet-tools.json`.

## Project structure

```text
WorkoutTracker.sln
├─ src/
│  ├─ WorkoutTracker.App/            Windows WPF application
│  ├─ WorkoutTracker.Application/   application services and use cases
│  ├─ WorkoutTracker.Domain/        domain model and rules
│  ├─ WorkoutTracker.Infrastructure/SQLite, EF Core, persistence, migrations
│  └─ WorkoutTracker.Mobile/        .NET MAUI Android presentation
├─ tests/
│  ├─ WorkoutTracker.Domain.Tests/
│  ├─ WorkoutTracker.Application.Tests/
│  ├─ WorkoutTracker.Infrastructure.Tests/
│  └─ WorkoutTracker.App.Tests/
└─ docs/
   ├─ android-development.md
   └─ WorkoutTracker_MultiDevice_Detailed_Design_v0.3_2026-09-22.md
```

Runtime databases, backups, Autosave/Active Draft data, logs, and generated build output are intentionally not included.

## Build and run

Restore packages once, then build the solution:

```powershell
dotnet restore .\WorkoutTracker.sln
dotnet build .\WorkoutTracker.sln -c Release --no-restore --nologo
```

Run the Windows WPF application:

```powershell
dotnet run --project .\src\WorkoutTracker.App\WorkoutTracker.App.csproj -c Release --no-restore
```

The Android project targets `net10.0-android`. Android-specific restore, build, packaging, installation, and run notes are in [`docs/android-development.md`](docs/android-development.md).

## Tests

Run the solution test projects with:

```powershell
dotnet test .\WorkoutTracker.sln -c Release --no-restore --nologo
```

Test results depend on the local .NET workload, SDK, package cache, and Windows/WPF environment. This snapshot records the source and test structure; a successful build or test run should not be inferred from the presence of the files alone.

## Development process

Most of the code has been implemented with an AI agent as the primary coding partner. The user remains responsible for requirements and design organization, architectural decisions, review, test planning and execution, human/device acceptance, and understanding the resulting code. This repository preserves that collaborative development state without claiming that the AI acted autonomously or that all planned work is finished.
