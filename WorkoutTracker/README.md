# WorkoutTracker — v1.0 Release Candidate

WorkoutTracker is a v1.0 release candidate for Windows (WPF) and Android (.NET MAUI), with shared Domain, Application, and Infrastructure layers backed by SQLite and Entity Framework Core.

## Implemented scope in this candidate

- FR-01: edit the date of a completed workout while retaining its history snapshot.
- FR-02: explain RM / estimated 1RM and RPE in the Windows and Android interfaces.
- FR-03: weekly schedules, rest days, and starting a workout from a schedule.
- FR-04: a shared catalog of 12 muscle groups and 100 standard exercises, with search and primary-muscle filters.
- FR-05: coordinated UI improvements across workout entry, statistics, history, calendar, and schedule screens.
- Shared local persistence, backup/restore, migration recovery, and `.wtb` transfer support.

## Projects

```text
src/WorkoutTracker.App/             Windows WPF
src/WorkoutTracker.Mobile/          .NET MAUI Android
src/WorkoutTracker.Domain/           Domain model and rules
src/WorkoutTracker.Application/      Shared services and use cases
src/WorkoutTracker.Infrastructure/   SQLite, EF Core, migration, backup, transfer
 tests/                              Domain, Application, Infrastructure, and WPF tests
 docs/                               Design and verification records
```

## Build and test

```powershell
dotnet restore .\WorkoutTracker.sln
dotnet build .\WorkoutTracker.sln -c Release --no-restore --nologo
dotnet test .\WorkoutTracker.sln -c Release --no-restore --nologo
```

For Android release build instructions, see [docs/android-development.md](docs/android-development.md). Current verification evidence and limitations are recorded in [docs/V1_Integration_Report_2026-11-03.md](docs/V1_Integration_Report_2026-11-03.md), [docs/NFR-004_Measurement_2026-11-02.md](docs/NFR-004_Measurement_2026-11-02.md), and [docs/Android_Release_Build_2026-11-02.md](docs/Android_Release_Build_2026-11-02.md).

## Release status

This branch is a **v1.0 release candidate**, not a formally released or production-approved version. The latest recorded integration run reports 272 automated tests passing and Windows/Android Release builds succeeding, with the NFR-004 parallel-run sensitivity documented. The latest source commit must be revalidated before distribution.

Not yet established by this repository check: migration against personal production databases, Android physical-device acceptance, full manual acceptance of every screen, and robust NFR-004 margin under concurrent load. Do not infer those gates from compile or automated test results.

Runtime databases, backups, test data, logs, build intermediates, and signing keys are excluded from source release archives.
