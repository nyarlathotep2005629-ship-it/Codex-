# WorkoutTracker テスト実行結果 2026-10-21

## 判定

| Check | Status | Evidence |
|---|---|---|
| テスト対応表の再生成 | PASS | 修正後の `Generate-TestTraceability.ps1` が227レコードを生成。Fact 216、Theory 11、InlineData 35、展開後251ケース |
| `dotnet restore` | PASS | .NET SDK 10.0.400、9プロジェクト復元 |
| 初回 `dotnet build -c Release` | FAIL / BLOCKED | Android AAPT2が `xml_microsoft_maui_essentials_fileprovider_file_paths.xml.flat` を開けず、ソリューション全体は失敗（2026-10-21、時刻未記録） |
| レビュー時 `dotnet build -c Release` | PASS | 2026-10-21 21:25:31–21:25:38 JST。Mobileを含むソリューション全体、警告0・エラー0 |
| 初回 Domain / Application / App | PASS | 19/19、28/28、45/45 |
| 初回 Infrastructure.Tests | FAIL | 158 PASS、1 FAIL、合計159。`NFR-004` の性能閾値超過 |
| レビュー時ソリューション全体 `dotnet test` | PASS | 2026-10-21 21:25:38–21:27:35 JST。19 + 28 + 45 + 159 = 251ケースPASS |
| `NFR-004` 受入判定 | FAIL | 閾値内の実行もある一方、1000ms超過を複数回再現。閾値・テスト対象は変更していない |
| Android AVD / 実機 | NOT RUN | 端末・AVDの実行結果は未取得 |
| Human acceptance | NOT RUN | 今回の監査では実施していない |

## 対象

- Repository: `Codex-/WorkoutTracker`
- Baseline: GitHub `main` の監査時点取得コミット（作業開始前に再確認）
- テストメソッド相当: 227（Fact 216、Theory 11）
- 実行ケース: 251（Fact 216 + InlineData 35）
- レビュー実行環境: Windows 10.0.26200 x64、論理プロセッサ16、.NET SDK 10.0.400、.NET Runtime 10.0.11、Microsoft.EntityFrameworkCore.Sqlite 10.0.11。
- 実行場所: `C:\Users\hiroki\Documents\Codex\2026-10-21\github-pr-gpt-5-6-sol\work\Codex-workout\WorkoutTracker`

## 実行記録

### 初回記録（AAPT2失敗を含む）

- 日付: 2026-10-21
- 時刻: 元ログに開始・終了時刻がなく `NOT AVAILABLE`
- 環境: Windows、.NET SDK 10.0.400、隔離worktree
- Build: `FAIL / BLOCKED`。Android AAPT2が `xml_microsoft_maui_essentials_fileprovider_file_paths.xml.flat` を開けず、Mobileを含むソリューション全体は失敗
- Infrastructure: 158 PASS / 1 FAIL。`EstimatedOneRepMax p95=1021.4ms limit=1000ms`
- NFR-004単独再実行: `EstimatedOneRepMax p95=1004.8ms limit=1000ms`

```powershell
dotnet restore .\WorkoutTracker.sln
dotnet build .\WorkoutTracker.sln -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.Domain.Tests\WorkoutTracker.Domain.Tests.csproj -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.Application.Tests\WorkoutTracker.Application.Tests.csproj -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.Infrastructure.Tests\WorkoutTracker.Infrastructure.Tests.csproj -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.App.Tests\WorkoutTracker.App.Tests.csproj -c Release --no-restore --nologo
```

この失敗記録は、後続のRelease Build成功で置き換えない。初回はAAPT2で失敗し、レビュー時には同一ブランチの再実行で成功した、別々の実行結果である。

### レビュー時記録（Release Build PASS）

- Build: 2026-10-21 21:25:31.615–21:25:38.234 JST、`PASS`、警告0、エラー0
- Test: 2026-10-21 21:25:38.238–21:27:35.145 JST、`PASS`、251/251
- コマンド:

```powershell
dotnet build .\WorkoutTracker.sln -c Release --no-restore --nologo
dotnet test .\WorkoutTracker.sln -c Release --no-restore --no-build --nologo --logger "console;verbosity=normal"
```

## NFR-004調査

既存テスト `AnalyticsQueryTests.StatisticsQueryPerformance` の条件と測定コードを確認した。

- DB規模: 5,000 Workout、各WorkoutにExercise 1件、20 Set、合計100,000 Set。日付は1,826日へ分散する。
- 対象: 1Y（上限500ms）とALL（上限1000ms）について、5種類の `StatisticsMetric` を測定する。
- ウォームアップ: 各期間・各metricにつき1回。
- 測定: ウォームアップ後に20回を `Stopwatch` で測り、昇順19番目をp95とする。seed処理は測定外である。
- 測定範囲: `GetStatisticsAsync` 呼出し全体。DB接続・SQL実行・集計結果のmaterializeとDTO生成を含む。
- 統計情報: NFR-003はbulk insert後に `ANALYZE` を実行するが、NFR-004は実行しない。

観測結果:

| 実行 | 結果 | 証跡 |
|---|---|---|
| 初回全体 | FAIL | `EstimatedOneRepMax p95=1021.4ms limit=1000ms` |
| 初回単独再実行 | FAIL | `EstimatedOneRepMax p95=1004.8ms limit=1000ms` |
| レビュー時全体（先行実行） | FAIL | `EstimatedOneRepMax p95=1023.8ms limit=1000ms` |
| レビュー時単独1回目 | PASS | PASS時はテストがp95実数を出力しないため値は未取得 |
| レビュー時単独2回目 | FAIL | `Sets p95=1010.7ms limit=1000ms` |
| 2026-10-21 21:25全体 | PASS | NFR-004を含む251/251。PASS時のp95実数は未出力 |

同一環境でもPASSとFAILが混在し、複数metricで1000ms付近を跨いだ。したがって環境負荷、OSスケジューリング、ファイルキャッシュ、SQLite接続状態などの変動影響は考えられるが、個別要因を測定分離していないため原因確定とはしない。ウォームアップ1回、20サンプルの19番目をp95とする設計も外れ値の影響を受け得るが、今回テスト条件・閾値・対象は変更していない。

受入判定は `FAIL` を維持する。最新1回のPASSから過去の超過を消去せず、安定して上限以内であることは未証明とする。

## 対応表集計の修正

`Generate-TestTraceability.ps1` は従来、1行に複数の `[InlineData(...)]` があっても1件として数えていた。正規表現の全一致件数を加算するよう修正し、CSVを再生成した。227メソッド、Fact 216、Theory 11、InlineData 35、展開後251ケースが実行結果と一致することを確認した。

## 未検証

- Android UI、AVD、実機、ADB、force-stop、cold start、端末ライフサイクル
- Windows DB unchangedの手動確認
- 設計書が参照するがGitHubに含まれないM1 Finalization / Stable Manifestの内容
- `NFR-004` の安定達成、OS/CPU/ストレージ負荷を統制した原因分離、別環境での比較測定
