# WorkoutTracker テスト実行結果 2026-10-21

## 判定

| Check | Status | Evidence |
|---|---|---|
| テスト対応表の再生成 | PASS | `Generate-TestTraceability.ps1` が227レコードを生成 |
| `dotnet restore` | PASS | .NET SDK 10.0.400、9プロジェクト復元 |
| `dotnet build -c Release` | FAIL / BLOCKED | Domain/Application/Infrastructure/WPFとテストDLLは生成。Android AAPT2が `xml_microsoft_maui_essentials_fileprovider_file_paths.xml.flat` を開けず、ソリューション全体は失敗 |
| Domain.Tests | PASS | 19/19 |
| Application.Tests | PASS | 28/28 |
| Infrastructure.Tests | FAIL | 158 PASS、1 FAIL、合計159。`NFR-004` の性能閾値超過 |
| App.Tests | PASS | 45/45 |
| ソリューション全体 `dotnet test` | BLOCKED | Release buildがMobileで失敗したため、ソリューション一括テストは完了扱いにしない |
| Android AVD / 実機 | NOT RUN | 端末・AVDの実行結果は未取得 |
| Human acceptance | NOT RUN | 今回の監査では実施していない |

## 対象

- Repository: `Codex-/WorkoutTracker`
- Baseline: GitHub `main` の監査時点取得コミット（作業開始前に再確認）
- テストメソッド相当: 227（Fact 216、Theory 11）
- 実行ケース: 251（Fact 216 + InlineData 35）
- 実行環境: Windows、.NET SDK 10.0.400。管理者相当の実行許可が必要なため、隔離環境で復元・ビルド・テストを実施。

## 実行記録

実行した主なコマンド:

```powershell
dotnet restore .\WorkoutTracker.sln
dotnet build .\WorkoutTracker.sln -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.Domain.Tests\WorkoutTracker.Domain.Tests.csproj -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.Application.Tests\WorkoutTracker.Application.Tests.csproj -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.Infrastructure.Tests\WorkoutTracker.Infrastructure.Tests.csproj -c Release --no-restore --nologo
dotnet test .\tests\WorkoutTracker.App.Tests\WorkoutTracker.App.Tests.csproj -c Release --no-restore --nologo
```

個別テストの結果は上表のとおり。Infrastructureの失敗は次の既存テストによる。

- ID: `NFR-004`
- Test: `AnalyticsQueryTests.StatisticsQueryPerformance`
- 初回結果: `EstimatedOneRepMax p95=1021.4ms limit=1000ms`
- 単独再実行: `EstimatedOneRepMax p95=1004.8ms limit=1000ms`
- 判定: `FAIL`。2回とも閾値を超過したため再現性ありと扱う。閾値を緩和したり、失敗を除外したりしていない。

ソリューション全体のRelease buildは、テストプロジェクトの成果物生成後にAndroid AAPT2で失敗した。したがって、個別テストのPASSからMobileを含むソリューション全体のPASSを推論しない。

## 未検証

- Android UI、AVD、実機、ADB、force-stop、cold start、端末ライフサイクル
- Windows DB unchangedの手動確認
- 設計書が参照するがGitHubに含まれないM1 Finalization / Stable Manifestの内容
- `NFR-004` の性能閾値超過の原因分析と再実行による再現性確認
