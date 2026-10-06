# Workout Tracker Multi-device Detailed Design v0.3

更新日: 2026-09-22  
位置付け: v0.2を基礎にしたM1確定差分。M2の同期設計は含めない。

## 1. Platform boundary

- Windows: `WorkoutTracker.App` / WPF
- Android: `WorkoutTracker.Mobile` / .NET MAUI Android
- Shared: `WorkoutTracker.Domain`、`WorkoutTracker.Application`、`WorkoutTracker.Infrastructure`
- Domain / Application の共有契約を境界とし、PresentationとOS固有の起動・ファイル処理は各プラットフォームに分離する。

## 2. Android Local First

Androidはapp-private SQLiteを使用する。Domain、Application、既存EF Core modelを共有し、Migrationは`InitialCreate`を使用する。WALとGuid TEXT identityを維持し、Android DBはWindows user DBと独立する。Android UIから`DbContext`を直接利用しない。M1では同期、転送、共有DBを扱わない。

## 3. Build / runtime boundary

- Target: `net10.0-android`
- Minimum API: 24
- M1 validation: API 36
- JDK: 17
- Android SDK: `C:\Android\Sdk`
- Windows上のAndroid build entry: `C:\WorkoutTrackerMobileSpike`（ASCII-only junction）
- 日本語Repository Pathで発生し得るaapt2 compatibility問題を避けるworkaroundとしてASCII junctionを使用する。

## 4. Master Data

Fresh Install後にAndroid UIから以下を作成・編集できる。

- Muscle Group
- Exercise
- Secondary Muscle
- Active / hidden Exercise
- Workout Menu
- MenuExercise
- PlannedSetCount
- DisplayOrder

既存のMasterDataServiceとApplication契約を再利用し、MenuExerciseの順序・予定セット数・参照済みExerciseの非表示 semanticsを維持する。

## 5. Workout / persistence

Draftは`ActiveWorkoutState`で表し、`AutoSaveCoordinator`が500 ms debounceで保存する。commit済みSQLite Draftをdurable SoTとし、debounce前の未commit editにはloss windowがある。lifecycle callbackをdurability guaranteeとしない。force-stop / cold-startではSQLiteからDraftをrehydrateする。Finish後はCompleted stateを保存する。

## 6. History / snapshot safety

HistoryはCompletedのみを対象に、keyset paginationと既存filterを使用する。Detailは完了時のsnapshotを表示し、current Masterを過去Workoutへ再適用しない。Snapshotの種目名・主部位名はMaster変更後も不変とする。

## 7. Calendar

月Query、Workout marker、selected-date summaryを提供する。日別summaryからHistory detailへ遷移し、Completed SQLite queryを再読込する。DraftはCalendar markerやCompleted KPIに含めない。

## 8. Home Dashboard

Completed-only KPI、Recent Workout、Workout frequency、Draft Resumeを提供する。未完了DraftはCompleted KPIから除外する。

## 9. Statistics

既存`AnalyticsService`を利用し、Estimated 1RM、Max Weight、Volume、Sets、Repsを表示する。チャートはMAUI Graphicsを使用する。Repsは既存仕様の「日別最大レップ数」であり、Total Repsではない。

## 10. Rest Timer

Local UI serviceとして実装し、`TargetEndUtc`をSoTとする。resume時に現在時刻との差分を再計算し、Ticker減算によるdriftを避ける。M1ではExact Alarm、notification、Foreground Service、process-kill後のTimer persistenceを実装しない。

## 11. Navigation

Primary navigationは`Home / Workout / History / Statistics / More`。More配下に`Calendar`、`Training Data`を置き、将来のData / Settings entries用の拡張余地を残す。

## 12. M1 acceptance record

M1 Finalization Report 2026-09-22の確定EvidenceをSoTとする。true Fresh AVD、Product UI Golden Path、force-stop / rehydrate、Snapshot safety、Statistics QA、final cold start、234 full regression PASS、Android Release PASS、Windows DB unchangedを受入結果とする。

## 13. M1 explicit boundaries

以下はM1 Stableに含めない: Sync、LAN、Kestrel、Pairing、TLS、`.wtb`、Manual Transfer、SyncVersionToken、DeviceId、ChangeJournal、JournalEpoch、Tombstone、PeerCheckpoint、PendingConflict、Merge Engine、Conflict Resolver、Android Backup/Restore UI、Timer notification、Exact Alarm、Foreground Service、Completed Edit/Delete。これらはfuture / M2-M3 boundaryとしてのみ記録する。

## 14. Stable freeze reference

判定は **Phase M1 Android Local: PASS / Stable Accepted**。詳細Evidenceは`WorkoutTracker_Android_M1_Finalization_Report_2026-09-22.md`、配布・復帰情報は`WorkoutTracker_Android_M1_Stable_Manifest_2026-09-22.md`を参照する。
