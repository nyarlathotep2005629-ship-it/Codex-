# HipHopMax 基本設計書

- 文書版数: v0.3
- 作成日: 2026-08-15
- 対象: HipHopMax v1.0
- 前提: `01_requirements.md`

## 1. 設計方針

HipHopMaxは「普通のマキシマイザー + 正確なラウドネスメーター」を基本とする。

設計上の優先順位は以下とする。

1. 音声処理を止めない。
2. 100% Wet経路のCeilingをSample Peakとして確実に守る。
3. DSPをJUCE/VST3統合層から分離する。
4. Meterは規格実装を利用し、独自再実装を避ける。
5. UIは1画面で完結させる。
6. Hip-Hop GuideとStreaming Referenceを「参考値」として表示し、自動制御には利用しない。

## 2. システム全体構成

```text
Cakewalk Sonar
      |
      | VST3
      v
+-----------------------------+
| PluginProcessor             |
|  Host I/O / Parameters      |
+-------------+---------------+
              |
              v
+-----------------------------+
| MaximizerDSP                |
|  Drive                      |
|  Raw / Wet Lookahead Delay  |
|  5 ms Attack Ramp           |
|  Gain Envelope              |
|  Sample Ceiling Safety      |
|  Bypass Delay/Crossfade     |
+-------------+---------------+
              |
              +-------------------------> Audio Output
              |
              v
+-----------------------------+
| Meter Tap / SPSC FIFO       |
+-------------+---------------+
              |
              v
+-----------------------------+
| MeterWorker                 |
|  libebur128                 |
|  LUFS-I / S / M             |
|  True Peak                  |
+-------------+---------------+
              |
              v
+-----------------------------+
| Atomic MeterSnapshot        |
|  Fields + Sequence Counter  |
+-------------+---------------+
              |
              v
+-----------------------------+
| PluginEditor                |
|  One-screen GUI / GR atomic |
+-----------------------------+
```

## 3. レイヤ構成

### 3.1 Host Integration Layer

責務:

- VST3としてCakewalk Sonarと接続
- Audio/MIDI Bus定義
- Sample Rate / Block Size通知
- Latency通知
- Parameter Automation
- State保存/復元
- APVTS `bypass`を`getBypassParameter()`から返しVST3 `kIsBypass`へ接続
- `processBlockBypassed()`でも5 ms Raw Dry Delayを維持

主クラス:

- `PluginProcessor`
- `ParameterState`

### 3.2 DSP Layer

JUCEに依存しない純粋C++を基本とする。

責務:

- Drive
- Lookahead Delay
- Stereo-linked peak detection
- 5 ms linear lookahead attack ramp (PoC candidate)
- Gain Reduction
- Release
- Sample Peak Ceiling保証
- Raw/Wet分離DelayによるBypass時Latency維持

主クラス:

- `MaximizerDSP`
- `LookaheadBuffer`
- `GainEnvelope`
- `ParameterSmoother`

### 3.3 Metering Layer

責務:

- DSP出力のMeter用コピーを非同期処理
- LUFS-I / S / M
- True Peak
- Meter Validity管理
- Histogram ModeのBounded-memory運用
- Worker stop/join/reprepare lifecycle

主クラス:

- `MeterFIFO`
- `MeterWorker`
- `EbuR128Meter`
- `MeterSnapshot`
- `MeterSnapshotPublisher`

### 3.4 UI Layer

責務:

- Parameter操作
- Meter表示
- Hip-Hop Guide表示
- Streaming Reference表示
- Meter Session Reset
- DSPからAtomicで取得したGain Reduction表示

主クラス:

- `PluginEditor`
- `MainView`
- `LoudnessScale`
- `MeterPanel`

## 4. 音声信号フロー

```text
Raw Input L/R
   |
   +----> Raw 5 ms Delay --------------------------+
   |                                               |
   +----> Drive ----> Peak Detector                 |
              |              |                     |
              |              v                     |
              |       5 ms Linear Attack Ramp      |
              v              |                     |
        Wet 5 ms Delay --> Gain/Sample Safety      |
                             |                     |
                             +--> Wet Output -------+--> Bypass Crossfade
                                                        |
                                                        +--> Final Output --> Meter FIFO

Limiter DSP -- atomic current GR -----------------------> GUI
```

### 4.1 Lookahead

Lookaheadは5 ms固定とする。

`lookaheadSamples = round(sampleRate * 0.005)`

この値をホストへLatencyとして報告する。

RawとDrive後Wetは別のDelay経路を持ち、同一Sample位置でDry/Wet Crossfadeできるようにする。選択アーキテクチャにPeak WindowまたはMonotonic Dequeは使用しない。

### 4.2 Stereo Link

Stereoでは左右の大きい方のAbsolute Peakを検出し、左右に同じGainを適用する。

v1.0ではStereo Link 100%固定とする。

### 4.3 Ceiling

CeilingはSample Peakに対するdBFS値である。

True Peakは別メーターとして表示し、Limiter制御には用いない。

Ceiling保証はBypass Crossfadeが100% Wetのときだけ適用する。Dry成分を含む期間は保証対象外である。

## 5. Parameter基本設計

| Parameter ID | UI名 | 範囲 | Default | Automation |
|---|---|---:|---:|---|
| `drive` | DRIVE | 0 to +18 dB | 0 dB | Yes |
| `ceiling` | CEILING | -6 to 0 dBFS | -1 dBFS | Yes |
| `release` | RELEASE | 10 to 1000 ms | 100 ms | Yes |
| `bypass` | BYPASS | Off / On | Off | Yes / Host Bypass共通 |

v1.0のAutomationはBlock Rateであり、各Block先頭でtargetを1回取得する。Drive等の内部Smoothingは継続するが、Sample Accurate Automationは行わない。

非Audio Parameter:

- `referencePlatform`
- `meterResetGeneration`
- `meterAppliedGeneration`
- Meter値

## 6. Meter基本設計

Meteringにはlibebur128 1.2.6を採用し、PoC Mode候補を次で固定する。

```text
EBUR128_MODE_S
| EBUR128_MODE_I
| EBUR128_MODE_TRUE_PEAK
| EBUR128_MODE_HISTOGRAM
```

理由:

- M / S / Iモード対応
- True Peak対応
- 複数Sample Rate対応
- MIT License
- EBU R128の既存実装を利用できる

Audio ThreadからMeter処理を分離する。

```text
Audio Thread
    |
    | non-blocking write
    v
SPSC Ring Buffer
    |
    v
Meter Worker Thread
    |
    v
libebur128
    |
    v
Multiple atomic fields + sequence counter
    |
    v
GUI Thread
```

Meterが遅延してもAudio Threadは待たない。

Gain Reductionはこの経路へ含めず、Limiter DSPのAtomic最新値をGUIが20から30 Hz程度で読む。

Snapshot Writerは非Audio Thread用Writer Mutexで直列化し、Sequenceを奇数へ更新してから各Atomic Fieldを書き、最後に偶数へ更新する。ReaderはLockを取らず、前後のSequenceが同じ偶数の場合だけSnapshotを採用する。これによりWorker、Reset UI、Lifecycle ThreadからのPublishが競合してもLUFS/True Peak/Status/GenerationのTorn Readを防ぐ。Audio ThreadはこのMutexを使用しない。

Histogram ModeはIntegrated履歴のMemoryをBoundedにするPoC候補であり、EBU代表ケースによる精度試験と長時間Memory非増加試験の両方を通過して初めて採用確定とする。

## 7. LUFS測定セマンティクス

- LUFS-M: Momentary、約400 ms
- LUFS-S: Short-term、約3秒
- LUFS-I: Meter Session Resetから現在まで

RESETはLUFS-Iだけでなく、LUFS-S/Mの履歴とValidity、True Peak最大値、Overrun INVALIDを含むMeter Session全体を新規開始する。

Reset操作は`meterResetGeneration`を増加させ、Workerの状態に関係なく`MeterSnapshotPublisher`から新Generationの全Field `unmeasured` Snapshotを即時公開してからWorkerをWakeする。Worker停止中はReset適用をPendingとして保持し、次回起動時にlibebur128を再初期化する。

True Peak表示はSession Reset後から現在までの最大値とする。LUFS-I / S / M / True Peakは項目別Validityを持つため、必要な測定長へ達した項目から順に有効化できる。

各項目は`unmeasured` / `valid` / `silence` / `error`のStatusを持つ。UIは順に`--` / 数値 / `-∞` / `ERR`を表示する。API成功時の無音`-Inf`は`silence`、その他の非有限値またはAPI Errorは`error`とし、非有限値自体を数値Fieldへ保存しない。

True Peakは`ebur128_true_peak()`でL/Rを取得し、その最大値をSession最大値へ反映する。

Stop、Pause、Loop、シークでは自動リセットしない。

## 8. Streaming Reference設計

Streaming Referenceはマスタリングの自動ターゲットではなく、現在値と配信時の再生基準を比較するための表示である。

```text
Streaming Reference
[ Amazon Music v ]   approx. -14 LUFS  [INDUSTRY REF]
```

データ構造は、値だけでなく信頼区分を持つ。

```text
Reference
- id
- displayName
- integratedLufs (optional)
- sourceClass: Official / IndustryReference / Unavailable
- note
```

初期値:

| Service | Reference | Class |
|---|---:|---|
| Spotify | -14 LUFS | Official |
| Apple Music | approx. -16 LUFS | IndustryReference |
| Amazon Music | approx. -14 LUFS | IndustryReference |

UI上でOfficialとIndustry Referenceを視覚的に区別する。

## 9. UI基本設計

1画面構成とする。

```text
+------------------------------------------------+
| HIPHOP MAXIMIZER                        BYPASS |
|                                                |
|                  -8.1                          |
|                  LUFS-I                        |
|                 [RESET]                        |
|                                                |
| -18     -14        -10     -9======-7     -5  |
|          ^                     HIPHOP GUIDE     |
|      Streaming                                  |
|                                                |
| Reference: [Amazon Music v] approx -14         |
|                                                |
| LUFS-M       LUFS-S       TP          GR       |
| -7.3         -7.8        -1.2       -3.4       |
|                           dBTP         dB       |
|                                                |
|       DRIVE       CEILING       RELEASE        |
|         O             O             O           |
|       +4.2          -1.0          100 ms       |
+------------------------------------------------+
```

UI設計原則:

- 基本画面にAdvanced Panelを置かない
- 最重要値はLUFS-I
- CeilingはdBFS、True PeakはdBTPと単位を明記
- Hip-Hop Guideは「GUIDE」と表示する
- Streaming値は「REFERENCE」と表示する

## 10. Latency / Bypass設計

Effect ON:

```text
Raw Input -> Drive -> Wet 5 ms Delay -> Limiter -> Wet Output
```

Effect OFF:

```text
Raw Input -> Raw 5 ms Delay -> Dry Output
```

Raw DelayとWet Delayは同一Latencyを持つ。Bypass切替時は短いCrossfadeを入れ、クリックノイズを低減する。Dry経路にDriveまたはLimiterを混入させない。

Ceiling保証はCrossfade係数が100% Wetのときだけ成立し、DryまたはCrossfade中は保証しない。

ホストへ報告するLatencyはEffect ON/OFFで変更しない。

APVTSの`bypass`はPlugin内Button、Cakewalk Host Bypass、VST3 `kIsBypass`の唯一のSource of Truthとする。`getBypassParameter()`はこのParameterを返す。Hostが`processBlockBypassed()`を呼ぶ場合も共通Bypass DSP経路を使用し、既定の無遅延Pass-throughにしない。

## 11. Host I/O境界

| 項目 | v1.0設計 |
|---|---|
| Stereo -> Stereo | Accept |
| Mono -> Mono | Reject |
| Stereo -> Mono | Reject |
| Mono -> Stereo | Reject |
| Surround | Reject |
| float32 | Supported |
| float64 | Not supported |
| MIDI | Not used |
| Variable block | Supported |
| Offline render | Audio DSP supported |

## 12. State設計

保存:

- Drive
- Ceiling
- Release
- Bypass
- Streaming Reference選択
- `schemaVersion`

保存しない:

- LUFS-I / S / M
- True Peak
- Gain Reduction
- Meter Valid/Invalid

State Rootへ整数`schemaVersion`を必須保存する。v1.0のCurrent Schemaは1とし、旧Schemaは明示Migration、未知の新Schemaは安全なDefaultへFallbackして診断する。

## 13. エラー・異常設計

### Audio DSP

- NaN/Infinity inputは安全値へ置換
- Internal Gainが非有限になった場合は1.0へ復旧
- 異常時にもCrashを避ける

### Meter

- FIFO Overflow時はAudioを継続
- MeterをINVALIDにする
- Meter Sessionは`meterResetGeneration`更新後に再測定する
- API Errorは該当Fieldだけ`error`とし、非有限値をSnapshotへ保存しない

### 13.1 MeterWorker lifecycle

`prepareToPlay()`再実行時:

1. Audio ThreadからFIFOへの書込みをAtomic flagで停止。
2. Workerへ停止要求し、Audio Thread外でjoin。
3. FIFOをclear。
4. `meterResetGeneration`を更新。
5. libebur128 stateを新Sample Rate/Channel Countで再初期化。
6. Snapshotを新Generationかつ全Field `unmeasured`へ公開。
7. Workerを再開し、FIFO書込みを許可。

`releaseResources()`およびPlugin破棄ではWorkerを停止/joinする。EditorのOpen/CloseはWorker lifecycleへ影響させない。

## 14. ソース構成

```text
HipHopMax/
|-- CMakeLists.txt
|-- AGENTS.md
|-- docs/
|-- Source/
|   |-- PluginProcessor.cpp/.h
|   |-- PluginEditor.cpp/.h
|   |-- dsp/
|   |   |-- MaximizerDSP.cpp/.h
|   |   |-- LookaheadBuffer.cpp/.h
|   |   |-- GainEnvelope.cpp/.h
|   |   `-- ParameterSmoother.cpp/.h
|   |-- metering/
|   |   |-- MeterFIFO.cpp/.h
|   |   |-- MeterWorker.cpp/.h
|   |   |-- MeterSnapshotPublisher.cpp/.h
|   |   `-- EbuR128Meter.cpp/.h
|   |-- state/
|   |   `-- ParameterState.cpp/.h
|   `-- ui/
|       |-- MainView.cpp/.h
|       |-- LoudnessScale.cpp/.h
|       `-- MeterPanel.cpp/.h
`-- Tests/
    |-- LimiterTests.cpp
    |-- MeterTests.cpp
    `-- IntegrationTests.cpp
```

## 15. 採用技術

| 項目 | 固定値 |
|---|---|
| Language | C++20 |
| Framework | JUCE 9.0.0 |
| Meter | libebur128 1.2.6 |
| Plugin | VST3 x64 |
| Build | CMake >= 4.2 |
| Compiler | MSVC / Visual Studio 2026 |
| OS | Windows 11 x64 |
| Primary Host | Cakewalk Sonar |

Plugin metadata:

| 項目 | 値 |
|---|---|
| Plugin/Product Name | `HipHopMax` |
| Vendor/Company | `HipHopMax Audio` |
| VST3 Category | `Fx|Mastering` |
| `PLUGIN_MANUFACTURER_CODE` | `Hhpa` |
| `PLUGIN_CODE` | `Hmax` |

配布形態を一般公開/商用へ変更する場合は、その時点のJUCEライセンス条件を再確認する。

libebur128 1.2.6は`BUILD_SHARED_LIBS=OFF`で静的Buildする。同Versionの`cmake_minimum_required(VERSION 2.8.12)`はCMake 4.xでそのまま構成できないため、PoCではTop-levelをCMake 4.2以上とし、Third-party追加前だけ`CMAKE_POLICY_VERSION_MINIMUM=3.5`を設定する隔離案をClean Configure/Buildで検証する。失敗時のみ、依存CMakeListsへの最小Patch案を比較し、採用前に設計を更新する。

## 16. 基本設計上の重要判断

1. DSPはJUCEから可能な限り独立させる。
2. Lookaheadは5 ms固定でユーザーには見せない。
3. Stereo Linkは100%固定。
4. CeilingはSample Peak、True Peakは測定のみ。
5. Meterはlibebur128を採用する。
6. Meter失敗は音声処理へ波及させない。
7. Streaming Referenceは自動制御値ではない。
8. UIは1画面に限定する。
9. v1.0はStereo -> Stereo専用とする。
10. Lookahead制御はMonotonic Peak Windowを使わず、5 ms Linear Attack RampをPoC第一候補とする。
11. RESETはGenerationで順序付けたMeter Session Resetとする。
12. StateはSchema Versionを必須とする。
13. libebur128は静的リンクし、CMake 4.2+のPolicy互換をPoC Gateで確定する。
14. Host/GUI Bypassは単一APVTS Parameterへ統合し、Bypass callbackでもLatencyを維持する。
15. MeterSnapshotはAtomic Fields + Sequence Counterで公開する。
16. MeterWorkerはProcessor lifetimeへ結び、Editor lifetimeから分離する。
17. 5 ms Linear Attack RampはPoC候補であり、Safety Gainを100% Wet Ceiling保証の最終段とする。

## 17. 参考資料

- JUCE 9.0.0 Releases: https://github.com/juce-framework/JUCE/releases
- JUCE CMake example (3.22): https://github.com/juce-framework/JUCE/blob/master/examples/CMake/AudioPlugin/CMakeLists.txt
- CMake `cmake_minimum_required`: https://cmake.org/cmake/help/latest/command/cmake_minimum_required.html
- CMake `CMAKE_POLICY_VERSION_MINIMUM`: https://cmake.org/cmake/help/latest/variable/CMAKE_POLICY_VERSION_MINIMUM.html
- JUCE AudioProcessor latency API: https://docs.juce.com/master/classjuce_1_1AudioProcessor.html
- libebur128: https://github.com/jiixyj/libebur128
- EBU Loudness: https://tech.ebu.ch/loudness/
- Spotify Loudness Normalization: https://support.spotify.com/artists/article/loudness-normalization/
- Apple Sound Check: https://support.apple.com/en-ca/109331
- Amazon Music Loudness Normalization: https://digprjsurvey.amazon.co.uk/csad/help/node/TgG1d8mPjGwf885hRP
