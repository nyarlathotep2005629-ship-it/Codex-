# HipHopMax 依存・テスト音源の取得手順

更新日: 2026-10-21

## 固定する依存

| 依存 | 必須配置 | 固定版 | 検証方針 |
|---|---|---|---|
| JUCE | `.deps/JUCE-9.0.0` | 9.0.0 | 公式リリースアーカイブを取得し、アーカイブSHA-256を確認する |
| libebur128 | `.deps/libebur128-1.2.6` | 1.2.6 | 公式リポジトリの固定タグ/アーカイブを取得し、アーカイブSHA-256を確認する |
| EBU Loudness Test Set | `.downloads/ebu-v5.0-poc` | v5.0の9代表ファイル | EBU公式配布元から取得し、アーカイブと各WAVのSHA-256を確認する |
| CMake | `.tools/cmake/cmake-4.2.1-windows-x86_64` | 4.2.1 | 対象バージョンの実行ファイルを使う。PATHの別版へ黙って切り替えない |

公式参照先は `01_requirements.md`、`02_basic_design.md`、`03_detailed_design.md`、`04_test_plan.md` に記録されている。依存をGitHubリポジトリへ同梱せず、ライセンス・容量・更新責任を分離する。

## 既存記録に残るSHA-256

`BUILD_POC_RESULTS.md` に次の取得時点のハッシュが記録されている。これは今回の再取得を証明するものではなく、再取得時に比較する基準である。

- JUCE 9.0.0 archive: `bfd8dcd4af67223dedb4a26e9f3e9bea8960ed5528efdfbd891aded74ab05248`
- libebur128 1.2.6 archive: `edd60c1ed2ff45711ee1c372e723a1deda376ba9060c23fcc9262efddaee141b`
- EBU v5.0 archive: `9cc500b4df83f7c21855c74dce795ef5209a752bf884253ae57d0ce512efb062`

ハッシュ不一致、展開後ディレクトリ名不一致、タグ/版不一致の場合はBuildを開始せず `BLOCKED` とする。テストを通す目的で別版へ差し替えない。

## 再取得後の確認

```powershell
Get-FileHash .\path\to\JUCE-9.0.0.zip -Algorithm SHA256
Get-FileHash .\path\to\libebur128-1.2.6.zip -Algorithm SHA256
Get-FileHash .\path\to\ebu-loudness-test-set-v5.0.zip -Algorithm SHA256
Get-ChildItem .\.deps\JUCE-9.0.0\CMakeLists.txt
Get-ChildItem .\.deps\libebur128-1.2.6\CMakeLists.txt
Get-ChildItem .\.downloads\ebu-v5.0-poc\*.wav
```

上のコマンドはパスの存在とハッシュを確認するだけであり、実際のBuild/CTest PASSを意味しない。Build、CTest、Validator、Cakewalk、Listeningは別ゲートとして記録する。

## コストとCI判断

JUCE/MSVC/Windows SDKを必要とするBuildとEBU音源を使うMeterテストは、GitHub Actionsへ無条件に追加すると依存取得・キャッシュ・実行時間・ライセンス確認の負担が大きい。`MTR014_meter_long_run` はさらに常時CIへ入れない。まずは依存を安全に取得できるWindows runnerとキャッシュ方針を承認した後、軽量なconfigure/build/4 CTestを週次または手動workflowとして導入するのが妥当である。今回このリポジトリへCI workflowは追加していない。
