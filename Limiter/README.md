# HipHopMax v1.0.0

HipHopMax は、Windows x64 / VST3向けの個人使用ステレオ・マキシマイザーです。v0.3設計書を基準に実装し、2026-08-16時点の状態を個人使用版v1.0.0として固定しています。

## 現在の実装

- Stereo in / Stereo out、32-bit float processing
- 5 ms固定Lookahead
- Drive、Ceiling、Release
- Raw DryとWetを分離したLatency維持Bypass
- JUCE APVTSによるパラメータおよびState保存
- libebur128によるLUFS-M / LUFS-S / LUFS-I / True Peak測定バックエンド
- SPSC FIFO、Meter Worker、Generation Reset、整合Snapshot
- JUCE Generic Editorによる操作画面

カスタムMeter UIは未実装です。Meterバックエンドは存在しますが、v1.0.0の画面にはLUFS、True Peak、GR、Reset、Status表示を実装していません。

## 使用するVST3

Cakewalkへ導入するのは次の**ディレクトリ一式**です。内部の同名バイナリだけを取り出さないでください。

`artifacts\HipHopMax-v1.0.0-win-x64\HipHopMax.vst3`

管理者権限を使わない場合は、Cakewalkに登録済みの次のユーザー用検索先へディレクトリごとコピーできます。

`%LOCALAPPDATA%\Programs\Common\VST3\HipHopMax.vst3`

コピー後、CakewalkのVST設定から再スキャンしてください。現在の環境ではDocumentsも検索対象なので、成果物をプロジェクト内に置いたままでも認識できます。

## Release x64ビルド

必要環境:

- Windows x64
- CMake 4.2.1
- Visual Studio Build Tools 2026 / MSVC 19.51
- Windows SDK 10.0.26100
- JUCE 9.0.0: `.deps\JUCE-9.0.0`
- libebur128 1.2.6: `.deps\libebur128-1.2.6`

日本語を含む実体パスではJUCE補助ツールが失敗することがあるため、この環境ではワークスペースを指すASCII Junction `C:\Users\hiroki\Documents\HipHopMaxPoC` からビルドします。

PowerShell:

```powershell
Set-Location C:\Users\hiroki\Documents\HipHopMaxPoC
$cmake = '.\.tools\cmake\cmake-4.2.1-windows-x86_64\bin\cmake.exe'
$ctest = '.\.tools\cmake\cmake-4.2.1-windows-x86_64\bin\ctest.exe'

& $cmake -S . -B build\v1.0-release-x64 -G 'Visual Studio 18 2026' -A x64 -DCMAKE_CONFIGURATION_TYPES=Release
& $cmake --build build\v1.0-release-x64 --config Release --parallel 2
& $ctest --test-dir build\v1.0-release-x64 -C Release --output-on-failure
```

ビルド出力:

`build\v1.0-release-x64\HipHopMax_artefacts\Release\VST3\HipHopMax.vst3`

## 検証記録

実施結果、未実施項目、既知事項、SHA-256は [V1_0_RELEASE_RECORD.md](V1_0_RELEASE_RECORD.md) を参照してください。初期Build PoCの履歴は [BUILD_POC_RESULTS.md](BUILD_POC_RESULTS.md) に残しています。

