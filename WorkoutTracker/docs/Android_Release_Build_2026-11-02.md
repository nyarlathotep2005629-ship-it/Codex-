# Android Releaseビルド障害調査

調査日: 2026-11-02
基準コミット: `cfe225a08e823a855d6abafe52127bb1effc107b`
作業ブランチ: `codex/workouttracker-android-release`

## 結果

Android Releaseビルドは、ソースを短いパスに置いた独立worktreeで成功した。アプリ本体、MAUI設定、ApplicationId、署名設定、versionCodeは変更していない。元の深いパスにあるworktreeでは、Android生成物をCleanした後も同じAAPT2エラーを再現した。

| 条件 | AAPT2出力先フルパス長 | 結果 |
|---|---:|---|
| 元worktree `...\\worktrees\\workouttracker-phase2-nfr004\\WorkoutTracker` | 244文字 | `APT2098 / APT2261`で失敗 |
| 独立worktree `...\\worktrees\\wt\\WorkoutTracker` | 218文字 | Clean Releaseビルド成功 |
| 新規一時コピー `...\\Temp\\WTPhase2AndroidFresh` | 182文字 | Releaseビルド成功 |

同一ソース・同一SDKで作業パスを短くすると通過したため、主因はMAUI EssentialsのXML内容ではなく、AAPT2が扱う中間ファイルの長いパスと判断した。244文字のパス自体が256文字を超えたわけではない。AAPT2内部の追加パス処理を含む正確な上限条件はこの検証だけでは確定していないため、「256文字が厳密な境界」とは断定しない。

失敗対象はNuGet/MAUI依存AARから展開された次のリソース。

`src/WorkoutTracker.Mobile/obj/Release/net10.0-android/lp/168/jl/res/xml/microsoft_maui_essentials_fileprovider_file_paths.xml`

XMLは生成されており、内容をAAPT2単体で直接compileすると成功し、`.flat`ファイルが作られた。Clean後の元worktreeではMSBuildの`Aapt2Compile`タスク内で`.flat`が作られず、`APT2098`となり、続いて`APT2261`が報告された。ソースXMLやAndroidManifestの変更では解決しない。

関連資料:

- .NET AndroidのAAPT2エラーコード対応: <https://github.com/dotnet/android/blob/main/src/Xamarin.Android.Build.Tasks/Tasks/Aapt2.cs>
- 同一のMAUI Essentialsエラー文字列を含む報告: <https://github.com/dotnet/maui/issues/27814>

## 環境

- .NET SDK: 10.0.400
- .NET Runtime: 10.0.11 (Windows x64)
- `Microsoft.Maui.Controls`: 10.0.20 (`Directory.Packages.props`)
- .NET Android SDK pack: 36.1.69
- Android platform: android-36
- Android Build Tools: 36.0.0
- Android Platform Tools: 37.0.1
- Java: Microsoft OpenJDK 17.0.14
- NuGet restore: 成功

## 再現・受入結果

- 元worktreeでAndroid ReleaseをClean後にビルド: 失敗、`APT2098 / APT2261`を再現。
- 元のXMLをAAPT2単体でcompile: 成功。
- 新しいworktreeでRelease Androidビルド: 成功、警告0、エラー0。
- worktree移動後、再度CleanしてAndroid Releaseビルド: 成功、警告0、エラー0。
- Release全自動テスト: 251件合格、失敗0、スキップ0。
- Android実機インストール・画面操作: 未実施。

成功したビルド・テストのログは、このcheckout内のgitignore対象`work/`に保存した。元worktreeでの失敗詳細ログとbinlogは、既存の計測ブランチcheckout `C:\Users\hiroki\Documents\Codex\engineering-knowledge-base\worktrees\workouttracker-phase2-nfr004\WorkoutTracker\work\`にある。初回サンドボックス内テストはVSTest testhost接続タイムアウトで中止され、その後承認済み実行環境で全件を再実行してPASSを確認した。

## 運用上の対応

Android Releaseビルドでは、この短いcheckout/worktreeを使用する。元の深い作業パスでは同じ失敗が再発し得る。IDE利用時も、IDEでこのworktreeの`WorkoutTracker.sln`を開く。SDKやNuGet依存関係の一括更新、リソース内容の書き換えは不要。

実機試験と配布ストア向けの署名・パッケージ審査は別工程である。今回のビルド成功をそれらの確認済み結果として扱わない。
