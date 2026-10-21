# WorkoutTracker テスト仕様書 v1

更新日: 2026-10-21

## 1. 目的と適用範囲

本書は、GitHub上の `Codex-/WorkoutTracker` の現行 `main` から取得したソースに対して、既存自動テストの対象、実行方法、証跡の扱いを定義する。対象は次の4テストプロジェクトと、それらが参照する Domain / Application / Infrastructure / WPF 実装である。

- `WorkoutTracker.Domain.Tests`
- `WorkoutTracker.Application.Tests`
- `WorkoutTracker.Infrastructure.Tests`
- `WorkoutTracker.App.Tests`

テスト数を増やすこと自体を目的にしない。要件ID、テストID、テストコード、実行結果が追跡でき、実行していない確認を成功と扱わないことを優先する。

## 2. 現行資産の棚卸し

監査時点で、テストソースには `[Fact]` 216件と `[Theory]` 11件、合計227のテストメソッド相当がある。`[InlineData]` は35件であり、Theoryの実行ケース数はメソッド数227とは別に数える。

| テストプロジェクト | Fact | Theory | InlineData | 主な対象 |
|---|---:|---:|---:|---|
| App.Tests | 45 | 0 | 0 | WPF ViewModel、起動、テーマ、UI回帰、受入シナリオ |
| Application.Tests | 22 | 2 | 6 | Use case、Draft、Autosave、Rest timer、Shutdown、設計境界 |
| Domain.Tests | 13 | 2 | 6 | 計算、PR、入力値・ドメイン規則 |
| Infrastructure.Tests | 136 | 7 | 23 | SQLite、Migration/Recovery、Persistence、Analytics、Android M1相当、Transfer |
| **合計** | **216** | **11** | **35** | **227メソッド** |

正確なファイル・メソッド・DisplayName・IDの一覧は、[`TEST_TRACEABILITY.csv`](TEST_TRACEABILITY.csv) を参照する。このCSVは [`scripts/Generate-TestTraceability.ps1`](../scripts/Generate-TestTraceability.ps1) から再生成できる。

## 3. 対応付けのルール

既存テストの `[Fact(DisplayName = ...)]` / `[Theory(DisplayName = ...)]` に含まれるIDを、要件・設計上の識別子として抽出する。1つのテストが複数IDを検証する場合は、CSVの `TestIds` に複数値を保持する。IDがないテストは、メソッド名とDisplayNameを対象識別子として残し、未定義の要件IDを推測して付与しない。

対応関係の粒度は次のとおり。

```text
要件 / 設計上のID
  -> DisplayNameに記載されたテストID
  -> テストクラス / メソッド / ソース行
  -> 実行結果（今回の結果報告書）
```

この対応表は実装コードを変更しない。IDの意味そのものは既存の要求・設計資料を正本とし、DisplayNameだけから要件内容を新規に推定しない。

## 4. 検証レベル

| レベル | 対象 | 判定の意味 |
|---|---|---|
| Unit | Domain / Application | 実装された計算・状態遷移・ユースケースの自動テストが実行成功した |
| Integration | Infrastructure / SQLite / EF Core | DB、Migration、Persistence、Query、Recovery等のテストが実行成功した |
| WPF automated | App.Tests | WPF向け自動テストが実行成功した。実ユーザー操作の代替ではない |
| Android/device | `WorkoutTracker.Mobile` の実機・AVD | 端末、ADB、アプリ起動、画面操作、ライフサイクルを別途実行した場合のみ判定する |
| Human acceptance | 実機・人手確認 | 自動テストから推論せず、実行者とEvidenceを記録した場合のみ判定する |

`PASS` は当該Checkを今回実行して成功した場合だけに使用する。`FAIL`、`BLOCKED`、`NOT RUN`、`PARTIAL`、`USER-VERIFIED` を区別し、過去記録やファイル存在だけから現在のPASSを作らない。

## 5. 実行手順

リポジトリルートで実行する。

```powershell
dotnet restore .\WorkoutTracker.sln
dotnet build .\WorkoutTracker.sln -c Release --no-restore --nologo
dotnet test .\WorkoutTracker.sln -c Release --no-restore --nologo
```

対応表だけを再生成する場合:

```powershell
.\scripts\Generate-TestTraceability.ps1
```

Androidのビルド、インストール、AVD/実機確認は、[`android-development.md`](android-development.md) の環境条件を満たした場合に別Checkとして実行する。Windowsの `dotnet test` 成功をAndroid実機確認のPASSへ変換してはならない。

## 6. GitHubに存在しない証跡

設計書の12章・14章は、次のM1資料を参照し、`234 full regression PASS`、AVD、Product UI Golden Path、force-stop/rehydrate、Android Release、Windows DB unchanged等を受入結果として記載している。

- `WorkoutTracker_Android_M1_Finalization_Report_2026-09-22.md`
- `WorkoutTracker_Android_M1_Stable_Manifest_2026-09-22.md`

しかし、現行GitHubツリーには上記ファイルが存在しない。したがって、本リポジトリだけではそれらの結果、実行環境、対象コミット、ログを再確認できない。過去結果を再構成せず、今回の結果報告書では `NOT AVAILABLE IN THIS REPOSITORY` と扱う。

同名資料が別のローカル作業フォルダーに存在していても、GitHub上の証跡またはこのブランチの検証結果には自動採用しない。必要なら、公開してよい内容と個人パス・端末情報をレビューしたうえで、別作業として証跡の保存方針を決める。

## 7. 現時点の不足・優先度

| 優先度 | 不足 / リスク | 対応方針 |
|---|---|---|
| P0 | 現行mainでの全テスト実行結果がGitHub内にない | 今回の隔離ブランチで再実行し、結果報告書にコマンド・SDK・終了コードを記録する |
| P0 | 設計書が参照するM1報告書がGitHubにない | 推測で復元せず、未入手証跡として明記する |
| P1 | IDとソース行の一覧が手作業では更新漏れしやすい | CSV生成スクリプトを正本とし、テストソース変更時に再生成する |
| P1 | Android実機/AVD・人手受入の現在結果がない | 端末条件を満たす別Checkとして実行し、未実行はNOT RUNにする |
| P2 | 要件文書とIDの完全な機械的リンクは未整備 | 要件文書側に安定IDを付与する作業は、要件所有者レビュー後の別タスクとする |

## 8. 変更境界

本整備では既存の本番アプリケーション実装と既存テストの意図を変更しない。追加するのは、テスト資産の棚卸し・追跡・実行結果の記録に必要な文書と生成スクリプトである。新規テストが必要かどうかは、今回の実行結果と要件の突合後に判断する。

## 9. CI判断

現行GitHubにはActions workflowがない。Domain/Application/Infrastructure/Appの個別テストは軽量CI候補だが、現行ソリューション全体はAndroid AAPT2でビルド阻害が発生しているため、今回workflowを追加していない。まずMobile SDKの再現可能なrunner条件を確定し、個別.NETテスト、Mobile build、Android端末検証を別ジョブへ分離してから導入する。
