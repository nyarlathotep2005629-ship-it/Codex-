# NFR-004 計測基盤改善・実測報告

作成日: 2026-11-02 (JST)
基準: `main@cfe225a08e823a855d6abafe52127bb1effc107b`
作業ブランチ: `codex/workouttracker-nfr004-measurement`
対象: `tests/WorkoutTracker.Infrastructure.Tests/AnalyticsQueryTests.cs`

## 1. 変更内容

NFR-004テストを計測可能にし、しきい値・対象データ・metric・期間を変更せずに実行証拠を残す。

- Migration setup、bulk seed setup、行数取得、query plan診断、warmup、実測を別々に扱う。
- Migration時間とseed時間を報告し、5,000 Workout / 5,000 WorkoutExercise / 100,000 WorkoutSetをDBから数えて記録する。
- 1Y/ALL × 5 metricの各組でwarmup 1回、実測20回。実測区間は `IAnalyticsQuery.GetStatisticsAsync` 呼出しだけ。
- PASS/FAIL判定より前に、各組の全20 sample、p95、median、min/max/range、閾値、系列点の有無を書き出す。p95は既存のnearest-rank方式（20サンプルを昇順にし19番目）を維持。
- 実行環境、開始/終了UTC、SQLite/EF Core version、dataset件数、各統計クエリの `EXPLAIN QUERY PLAN` を `ITestOutputHelper` に出力。詳細console loggerでPASS時にも記録し、FAIL時は全10組の計測後に一括assertする。
- `ANALYZE` は全測定後だけ行い、前後の実行計画を比較する。NFR値は未解析状態の元テスト条件で測定するため、ANALYZEがtimed sampleに影響しない。
- NFR-003 / 他のテストや本体SQLを変更していない。

## 2. NFR-004測定結果

2026-11-02、同一端末のRelease構成で専用NFR-004を4回実行。全4回でテスト成功、全20 period/metric組（各20 samples）が閾値を満たした。下表は各反復のp95 (ms)。

| 期間 / metric | Run 1 | Run 2 | Run 3 | Run 4 | 4-run p95幅 | 閾値 |
|---|---:|---:|---:|---:|---:|---:|
| 1Y / EstimatedOneRepMax | 32.641 | 32.934 | 31.275 | 33.062 | 31.275–33.062 | 500 |
| 1Y / MaxWeight | 33.312 | 34.210 | 32.446 | 31.908 | 31.908–34.210 | 500 |
| 1Y / Volume | 29.748 | 32.089 | 32.978 | 30.925 | 29.748–32.978 | 500 |
| 1Y / Sets | 29.015 | 32.191 | 31.042 | 29.951 | 29.015–32.191 | 500 |
| 1Y / Reps | 27.481 | 31.080 | 29.863 | 28.527 | 27.481–31.080 | 500 |
| ALL / EstimatedOneRepMax | 511.106 | 560.449 | 559.231 | 530.636 | 511.106–560.449 | 1000 |
| ALL / MaxWeight | 511.396 | 547.810 | 557.654 | 536.771 | 511.396–557.654 | 1000 |
| ALL / Volume | 545.885 | 535.567 | 540.290 | 527.919 | 527.919–545.885 | 1000 |
| ALL / Sets | 546.114 | 510.338 | 535.067 | 512.559 | 510.338–546.114 | 1000 |
| ALL / Reps | 565.407 | 534.328 | 538.694 | 500.478 | 500.478–565.407 | 1000 |

Dataset件数は各Runで5,000 session、5,000 exercise、100,000 set。各RunのMigration setupは約0.66–0.89秒、seed setupは約6.74秒（両方とも測定対象外）。1Y最大p95は34.210ms、ALL最大p95は565.407ms。4回でNFR-004閾値超過は観測しなかった。これは当該端末・当該日・当該構成での測定結果であり、別端末、cold storage、負荷のある環境へ一般化しない。

### 環境

詳細Run logに各テストから出力された環境を記録。代表値:

- Windows 10.0.26200 x64、process/OS architecture x64、ProcessorCount 16
- ProcessorIdentifier: AMD64 Family 25 Model 117 Stepping 2, AuthenticAMD
- .NET SDK 10.0.400、Runtime / EF Core 10.0.11
- SQLite 3.53.3
- 5000 workout / 5000 exercise / 100000 set
- 期間/metricあたりwarmup 1回 + timed sample 20回

### 保存済み実行ログ

作業checkoutのignore対象 `work/` 下へ、生ログを保存した（Git追跡対象外）。

- `work/nfr004/repeat-1.txt` … Run 1、NFR PASS、環境・20×10 sample・query plans込み
- `work/nfr004/repeat-2.txt` … Run 2、NFR PASS
- `work/nfr004/repeat-3.txt` … Run 3、NFR PASS
- `work/nfr004/repeat-4.txt` … Run 4、NFR PASS
- `work/nfr004/run-status.txt` … 各Run exit code
- `work/release-build.txt` … Release solution buildの失敗ログ
- `work/release-tests.txt` … Release all-testsのPASSログ
- `work/restore-diagnostic.log` … 初回 sandbox restore失敗の詳細。NuGet restoreは後で承認済み実行にて成功

NFR-004の4回ではFAILは発生しなかったため、失敗NFR測定ログは今回の実行では生成されていない。失敗時の診断実装は、閾値判定前に全ての指標と全sampleを出力する。別の保存済みFAILとして、full Release solution buildはAndroid AAPT2失敗を記録している。

## 3. 実行計画・原因候補

Statisticsは `GetStatisticsAsync` ごとに概ね3クエリを実行する:

1. Exercise名取得
2. 全対象setの日別集約
3. 対象Workout数の `COUNT(DISTINCT s.Id)`

実行計画はANALYZE前後とも同じだった:

- 日付/状態: `IX_WorkoutSessions_History_Keyset` をcovering indexとしてrange scan
- Exercise: session-first joinの `UX_WorkoutExercises_Session_Exercise`
- Sets: `UX_WorkoutSets_Exercise_SetNumber`
- Workout数: `COUNT(DISTINCT)` 用 `USE TEMP B-TREE` が存在

ここで観測した約0.5秒のALL p95は主に100,000 setを対象とする日別集約と、その結果処理が占めると考えられるが、各SQL単体の時間は未計測なので内訳を原因確定扱いしない。`ANALYZE`前後の同一planだけから統計情報の影響がないと一般化もしない。少なくともこのfixture/SQLiteではplan表示に差はなかった。

過去の2026-10-21記録にはALL metricの1000ms超過（1004–1024ms付近）とPASSが混在している。今回の4回は500–565msで再現しなかった。以前の超過を無効化する根拠はなく、CPU負荷、storage/cache状態、他プロセス負荷、過去環境差などは候補だが、今回それらを統制比較していないため原因は未確定。今回のsample内のmax値は一部p95より大きい（最大約0.59秒）ためtail variationもログに残す。

## 4. 次の性能改善候補

大規模な本体変更を直ちに行う根拠はない。次は小さく比較できる順:

1. **SQLごとの時間計測を別benchmarkで行う**: 3 queryそれぞれを個別に20回計測。NFR統合計測のtimed pathは変更せず、どのqueryがALL時間を支配するか確認する。
2. **COUNT(DISTINCT) のcandidate検証**: `WorkoutSessionId + ExerciseId` のunique indexがあるため、同一Workoutに同一Exerciseは一意というDB制約を前提に `COUNT(*)` へ変更できるか、同じ結果を保証する境界テスト・query plan・p95を別ブランチで比較する。今回のplanではこのcountがtemp B-treeを使う。
3. **集約SQLのCPU負荷検証**: 日別queryはmetric選択に関わらず複数の `MAX(printf(...))` を計算する。推定1RM/最大重量/Reps用の決定的tie-break値形成がCPU costを生む可能性を計測する。ただしDTOは複数KPIを返すので単純に未選択metricを削ると契約を変える可能性があり、先に出力要件を確認する。
4. **環境差の再現**: background loadあり/なし、warm/cold OS file cache、別SSD/最低spec端末、反復回数を分けて測る。OS cacheを安全に操作する手段がない場合はcold条件を主張しない。
5. **index変更は根拠後に限定**: 現状のquery planは既存のCompleted/date、Session/Exercise、WorkoutSet indexesを利用。新index追加はDBサイズ/write costを測る具体的benefitが示された場合だけ評価。

受入閾値を緩める、測定対象を減らす、テストをskipする案は含めない。

## 5. Build・test結果

- NuGet restore: PASS（全9 project）
- Focused NFR-004: PASS 4/4独立Release runs。全20組のthreshold PASS。
- Release solution build: FAIL。Domain/Application/Infrastructure/WPFおよび全test assembliesはbuildされたが、Android MAUIでAndroid SDK 36.1.69 AAPT2が `xml_microsoft_maui_essentials_fileprovider_file_paths.xml.flat` を開けず `APT2098 / APT2261`。警告0/エラー2。失敗ログを保存。
- Release all automated tests: PASS 251/251、skip 0:
  - Domain 19
  - Application 28
  - App/WPF 45
  - Infrastructure 159
- `git diff --check`: PASS
- Android AVD/実機は今回未実施。Android release buildもAAPT2 errorにより未完了。

## 6. 変更ファイル

追跡対象変更:
- `tests/WorkoutTracker.Infrastructure.Tests/AnalyticsQueryTests.cs`
- `docs/NFR-004_Measurement_2026-11-02.md`

build/test logsは `work/` にあり.gitignore対象。署名鍵、ユーザーデータ、既存APK/AABは扱っていない。作業は独立ブランチに保持し、mainへはpush/mergeしていない。
