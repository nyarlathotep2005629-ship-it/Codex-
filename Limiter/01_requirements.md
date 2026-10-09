# HipHopMax 要件定義書

- 文書版数: v0.3
- 作成日: 2026-08-15
- 対象リリース: HipHopMax v1.0
- 文書状態: 設計レビュー反映版（PoC開始前ベースライン）

## 1. 目的

HipHopMaxは、Cakewalk Sonarで使用するWindows x64向けVST3マキシマイザーである。

本製品の目的は、一般的なLookahead Maximizerとして音圧を上げつつ、現在のラウドネスとピーク状態を同一画面で確認できるようにすることである。

特徴は機能の多さではなく、次の作業を単純に行えることに置く。

1. Driveを上げる。
2. LUFS-I / LUFS-S / LUFS-Mを見る。
3. Hip-Hop Guideと比較する。
4. True PeakとGain Reductionを確認する。
5. CeilingとReleaseを調整する。

## 2. 想定ユーザーと用途

主な想定用途はHip-Hop、Trap、Drillなどの2chステレオ楽曲のマスターバス処理である。

v1.0は初心者向け自動マスタリング機能を持たず、ユーザー自身が音を聴きながら調整する通常のマキシマイザーとする。

## 3. 対象環境

| 項目 | v1.0要件 |
|---|---|
| OS | Windows 11 x64 |
| DAW | Cakewalk Sonarを第一検証対象とする |
| プラグイン形式 | VST3 x64 |
| 対応バス | Stereo -> Stereoのみ |
| 対応外 | Mono、Surround、Mono/Stereo変換バス |
| サンプル精度 | 32-bit float |
| 検証サンプルレート | 44.1 / 48 / 88.2 / 96 kHz |

## 4. 機能要件

### FR-001 VST3ロード

Cakewalk Sonarが本プラグインをVST3として認識し、オーディオFXとして挿入できること。

### FR-002 リアルタイム音声処理

Stereo -> Stereoの音声をリアルタイム処理できること。v1.0ではMonoを受け付けない。

### FR-003 Drive

ユーザーがリミッター入力レベルを調整できること。

- 表示名: DRIVE
- 範囲: 0.0 dBから+18.0 dB
- 初期値: 0.0 dB
- DAW Automation: 対応

### FR-004 Ceiling

Sample Peakの最大レベルを指定できること。

- 表示名: CEILING
- 単位: dBFS
- 範囲: -6.0 dBFSから0.0 dBFS
- 初期値: -1.0 dBFS
- DAW Automation: 対応

v1.0のCeilingはTrue Peak Ceilingではない。

Ceiling保証はLimiter出力が100% Wetのときに限る。Bypass 100% Dry時およびBypass Crossfade中はRaw Dry成分を含むため、Ceiling超過を許容する。

### FR-005 Release

Gain Reductionからの復帰速度を調整できること。

- 表示名: RELEASE
- 範囲: 10 msから1000 ms
- 初期値: 100 ms
- DAW Automation: 対応

Release値は1-pole復帰Envelopeの時定数`tau`と定義する。例えば100 ms設定では、復帰開始から100 ms後に最終値までの差が初期差の約36.8%となる（最終値へ約63.2%接近する）。

### FR-006 Lookahead Limiter

内部に固定Lookaheadを持つSample-Peak Limiterとして動作すること。

- Lookahead: 5 ms固定
- v1.0ではユーザー操作項目にしない
- Lookahead相当のLatencyをホストへ報告する

### FR-007 Bypass

エフェクトをBypassできること。

Bypass時もLookahead分の遅延を維持し、ON/OFFで時間位置が変化しないこと。

Raw InputをDrive前に分岐してDry専用5 ms Delayへ送り、WetはDrive後の信号を別の5 ms Delayへ送る。Bypass出力は両者をCrossfadeし、Dry側へDriveまたはLimiter処理を混入させない。

APVTSの`bypass`を`AudioProcessor::getBypassParameter()`から返し、VST3 Wrapperで`kIsBypass`として公開する。Cakewalk側BypassとPlugin内Bypass UIは同一Parameterを操作する。

Hostが`processBlockBypassed()`を呼んだ場合も無遅延Pass-throughを行わず、Raw Dryの5 ms Delayと報告Latencyを維持する。

### FR-008 Gain Reduction表示

現在のGain ReductionをdBで表示すること。

Gain ReductionはLimiter DSPが算出し、Atomicな最新値としてGUIへ直接渡す。Meter FIFO、MeterWorker、libebur128を経由しない。

### FR-009 LUFSメーター

処理後の信号に対して以下を表示すること。

- LUFS-I: Integrated Loudness
- LUFS-S: Short-term Loudness
- LUFS-M: Momentary Loudness

測定方式はEBU R128 / ITU-R BS.1770系に沿うものとし、v1.0ではlibebur128を利用する。

PoCのlibebur128 Mode候補は次とする。

`EBUR128_MODE_S | EBUR128_MODE_I | EBUR128_MODE_TRUE_PEAK | EBUR128_MODE_HISTOGRAM`

Histogram ModeのM/S/I精度はEBU Loudness Test Set v5.0の代表ケースで検証し、長時間処理でMeter履歴Memoryが継続増加しないことを確認する。

LUFS-I / LUFS-S / LUFS-M / True Peakは各項目ごとにValid/Invalidを持ち、未確定の項目だけを無効表示できること。

表示状態は項目別に、未測定=`--`、無音=`-∞`、API/計算エラー=`ERR`を区別する。API成功かつ無音を示す`-Inf`はSilence Statusとし、それ以外の予期しない非有限結果またはAPI Errorは該当項目をError/Invalidとする。非有限float自体はSnapshotへ格納しない。

### FR-010 Meter Session Reset

ユーザー操作のRESETはMeter Session全体をリセットすること。

リセット対象:

- LUFS-Iの測定履歴
- LUFS-S / LUFS-Mの履歴と表示有効性
- Reset後から現在までのTrue Peak最大値
- Meter OverrunによるINVALID状態

再生開始、停止、Pause、Loop、シークでは自動リセットしない。

ResetはGeneration方式で順序付ける。停止中にResetされた場合もSnapshotを直ちに全項目未測定へ更新し、旧GenerationのFIFO FrameおよびSnapshotを新Sessionへ混入させない。

### FR-011 True Peak表示

処理後信号のTrue PeakをdBTPで表示すること。

v1.0ではTrue Peakを測定するが、True Peak Limitingは行わない。

表示値は最後のMeter Session Resetから現在までに観測した最大True Peakとする。Reset直後は無効とし、新しいサンプルを測定してから有効にする。

各更新では`ebur128_true_peak()`でL/Rを個別取得し、その大きい方をBlock観測値とする。Session最大True PeakはReset後のBlock観測値の最大とする。

### FR-012 Hip-Hop Guide

UI上に次の制作参考帯を表示すること。

- HIP-HOP GUIDE: -9 to -7 LUFS

この値は配信サービスの規格値ではなく、プラグイン内の制作参考値として明示する。

### FR-013 Streaming Reference

配信時のラウドネス基準を比較用Referenceとして表示できること。

v1.0では以下を表示候補とする。

| サービス | 表示値 | 表示区分 |
|---|---:|---|
| Spotify | -14 LUFS | Official normalization reference |
| Apple Music | 約 -16 LUFS | Industry reference |
| Amazon Music | 約 -14 LUFS | Industry reference |

Apple MusicとAmazon Musicは、公式資料でノーマライゼーション機能の存在を確認できる一方、本設計時点で固定LUFS値を公式一次資料から確認できないため、UIでは「INDUSTRY REF」等の表示で公式値と区別する。

これらのReferenceは自動マスタリングターゲットではない。選択してもDrive/Ceilingを自動変更しない。

### FR-014 1画面UI

主要機能を単一のメイン画面で操作・確認できること。

ユーザーが主に操作するもの:

- Drive
- Ceiling
- Release
- Bypass
- Meter Session Reset
- Streaming Reference選択

ユーザーが主に確認するもの:

- LUFS-I / LUFS-S / LUFS-M
- True Peak
- Gain Reduction
- Hip-Hop Guide
- Streaming Reference

### FR-015 状態保存

DAWプロジェクト保存時に少なくとも次を保存し、再ロード時に復元すること。

- Drive
- Ceiling
- Release
- Bypass
- Streaming Reference選択
- `schemaVersion`

メーター測定値は保存しない。

### FR-016 Offline Rendering

Cakewalk SonarのExport/Bounce/Freeze等のNon-Realtime処理でも、マキシマイザーの音声出力がRealtime処理と同一のDSP仕様で生成されること。

Meter表示はOffline Rendering結果の必須要件としない。

## 5. 非機能要件

### NFR-001 リアルタイム安全性

Audio Threadでは、動的メモリ確保、ファイルI/O、ネットワーク、GUIアクセス、待ちを伴うMutexを使用しない。Denormalによる異常なCPU負荷を防止する。

### NFR-002 安定性

無音、過大入力、長時間再生、Block Size変更、Sample Rate変更等でCrash、NaN、Infinity、異常発振を起こさないこと。

### NFR-003 Meter分離

Meter処理の遅延や失敗によって音声処理を停止させない。

Meter FIFOがOverflowした場合はMeterをINVALIDとし、Audioは継続する。

### NFR-004 Latency整合性

実際のLookahead Delayとホストへ報告するLatencyが一致すること。

### NFR-005 可変Block Size

固定Block Sizeを前提としない。

### NFR-006 保守性

DSP本体をJUCE/VST3のホスト統合コードから分離し、単体テスト可能にする。

### NFR-007 Automation粒度

v1.0のDrive / Ceiling / Release / Bypass AutomationはBlock Rateとする。Audio Block先頭でParameter targetを1回取得し、そのBlock内では同じtargetを使用する。Sample Accurate Automationはv1.0対象外とする。

### NFR-008 Meter Reset順序

Meter Resetは単純なBooleanではなく単調増加Generationで順序付ける。Reset前後のFIFOデータおよびSnapshotが混在しても、Reset前の測定値をReset後のSessionとして表示しない。

### NFR-009 Build再現性

libebur128 1.2.6は`BUILD_SHARED_LIBS=OFF`で静的リンクする。CMake 4.2以上で、依存側の古いPolicy Versionを`CMAKE_POLICY_VERSION_MINIMUM`により隔離して構成できることをPoC Gateで確認する。

### NFR-010 Snapshot整合性

MeterSnapshotは複数Atomic FieldとSequence Counterで公開する。Readerは読取り前後のSequenceが同じ偶数である場合のみ採用し、異なる更新世代の値が混ざったTorn Snapshotを表示しない。

### NFR-011 MeterWorker Lifecycle

`prepareToPlay()`再実行時は、FIFO書込み停止、Worker stop/join、FIFO clear、Generation更新、libebur128再初期化、Snapshot INVALID化、Worker再開の順に処理する。`releaseResources()`とPlugin破棄でもWorkerを停止/joinする。Editor開閉ではWorkerを停止しない。

### NFR-012 Histogram Memory上限

Histogram ModeのMeter stateはWarm-up後に履歴長へ比例した継続的Memory増加を起こさないこと。長時間Memory試験をPoC Gateへ含める。

### NFR-013 Plugin識別情報

v1.0の識別情報を以下で固定する。

| 項目 | 期待値 |
|---|---|
| Plugin/Product Name | `HipHopMax` |
| Vendor/Company Name | `HipHopMax Audio` |
| VST3 Category | `Fx|Mastering` (`VST3_CATEGORIES Fx Mastering`) |
| `PLUGIN_MANUFACTURER_CODE` | `Hhpa` |
| `PLUGIN_CODE` / JUCE Plugin Code | `Hmax` |

4文字CodeはPlugin identityであるため、公開Build後に不用意に変更しない。

## 6. v1.0完成条件

v1.0は以下を満たした時点で完成とする。

1. Cakewalk SonarがVST3として正常認識する。
2. Stereo音声をリアルタイム処理できる。
3. Driveで入力レベルを上げられる。
4. Ceilingを設定できる。
5. 100% Wet時のSample Peakが設定Ceilingを超えない。
6. 固定5 ms Lookahead Limiterが正常動作する。
7. Releaseを調整できる。
8. Bypass時もLatencyが維持される。
9. LUFS-I / S / Mを項目別Validity付きで表示できる。
10. Meter Session全体を手動Resetできる。
11. Reset後から現在までのTrue Peak最大値をdBTPで表示できる。
12. Gain Reductionを表示できる。
13. Hip-Hop Guideを表示できる。
14. Spotify / Apple Music / Amazon MusicのReference表示を切り替えられる。
15. Schema Version付きStateを保存し、対応する旧Schemaを安全に復元できる。
16. 44.1 / 48 / 88.2 / 96 kHzで検証を通る。
17. 可変Block Sizeで処理結果が破綻しない。
18. 長時間動作でCrash/NaN/Infinityが発生しない。
19. EBU Loudness Test Setによるメーター統合試験を通る。
20. Steinberg VST3 Validatorを通る。
21. Cakewalk Sonarで実際のHip-Hop楽曲を使った挿入、再生、Export試験を通る。
22. Audio Threadで動的Allocation、Blocking Lock、Denormal起因の異常負荷がない。
23. CMake 4.2以上でlibebur128 1.2.6の静的Build PoCを通る。
24. Cakewalk Host BypassとPlugin内Bypassが同一Parameterとして動作し、Bypass中も5 ms Latencyを維持する。
25. Torn Snapshot、Worker再準備/停止/破棄、停止中Reset Generationの競合試験を通る。
26. Histogram Modeが代表EBU Testに合格し、長時間Memoryが継続増加しない。

## 7. v1.0対象外

以下はv1.0には含めない。

- True Peak Limiter
- Clipper / Saturator
- EQ
- Multiband Compressor / Limiter
- Stereo Imager
- Bass Protect
- Auto Release
- Auto Loudness Match
- AI Mastering
- Reference Track自動解析
- Cloud / Account / DRM
- AU / AAX
- macOS
- Surround
- 64-bit double precision audio processing
- Mono入出力
- Sample Accurate Automation

## 8. 用語

| 用語 | 意味 |
|---|---|
| dBFS | デジタルフルスケール基準のサンプルピークレベル |
| dBTP | True Peakのレベル |
| LUFS-I | 測定開始からのIntegrated Loudness |
| LUFS-S | 約3秒のShort-term Loudness |
| LUFS-M | 約400 msのMomentary Loudness |
| GR | Gain Reduction |
| Lookahead | 実音声を遅延させ、未来のピーク情報を先に利用する処理 |
| Meter Session | 明示Resetから次のResetまでのLUFS/True Peak測定区間 |
| Generation | Reset要求とFIFO/Snapshotの前後関係を表す単調増加番号 |

## 9. 参考資料

- JUCE Releases: https://github.com/juce-framework/JUCE/releases
- libebur128: https://github.com/jiixyj/libebur128
- EBU Loudness: https://tech.ebu.ch/loudness/
- EBU Loudness Test Set: https://tech.ebu.ch/publications/ebu_loudness_test_set
- Spotify Loudness Normalization: https://support.spotify.com/artists/article/loudness-normalization/
- Apple Sound Check: https://support.apple.com/en-ca/109331
- Apple Digital Masters: https://www.apple.com/jp/apple-music/apple-digital-masters/
- Amazon Music Loudness Normalization: https://digprjsurvey.amazon.co.uk/csad/help/node/TgG1d8mPjGwf885hRP
- iZotope Streaming Loudness Overview (industry reference): https://www.izotope.com/en/learn/mastering-for-streaming-platforms.html
