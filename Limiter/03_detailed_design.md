# HipHopMax 詳細設計書

- 文書版数: v0.3
- 作成日: 2026-08-15
- 対象: HipHopMax v1.0
- 前提: `01_requirements.md`, `02_basic_design.md`

## 1. 詳細設計の目的

本書は、HipHopMax v1.0をCodexまたは人間の開発者が実装できる粒度まで、クラス、スレッド、DSP、状態、異常処理を定義する。

DSP音質に関する最終的な定数調整はPoC/試聴で行うが、入出力契約と安全条件は本書を正とする。

## 2. クラス構成

```text
PluginProcessor
|-- ParameterState
|-- MaximizerDSP
|   |-- ParameterSmoother
|   |-- LookaheadBuffer
|   `-- GainEnvelope
|-- MeterFIFO
|-- MeterWorker
|   `-- EbuR128Meter
`-- MeterSnapshotPublisher
    `-- Atomic MeterSnapshot fields + sequence

PluginEditor
|-- MainView
|-- LoudnessScale
`-- MeterPanel
```

## 3. `MaximizerDSP` インターフェース

```cpp
class MaximizerDSP
{
public:
    void prepare(double sampleRate, int maxBlockSize, int numChannels);
    void reset() noexcept;

    void setDriveDb(float value) noexcept;
    void setCeilingDb(float value) noexcept;
    void setReleaseMs(float value) noexcept;
    void setBypass(bool enabled) noexcept;

    void process(float* const* channels, int numChannels, int numSamples) noexcept;

    int getLatencySamples() const noexcept;
    float getGainReductionDb() const noexcept;
};
```

制約:

- `process()`はnoexcept相当の安全性を目標とする。
- `process()`内でHeap Allocationを行わない。
- `prepare()`で必要なBufferを確保する。

### 3.1 `PluginProcessor` Host Bypass契約

```cpp
juce::AudioProcessorParameter* getBypassParameter() const override;
void processBlockBypassed(juce::AudioBuffer<float>&,
                          juce::MidiBuffer&) override;
```

- `getBypassParameter()`はAPVTSのParameter ID `bypass`を返す。
- JUCE VST3 WrapperはこのParameterを`kIsBypass`としてHostへ公開する。
- Plugin内Bypass Buttonも同じAPVTS ParameterへAttachmentする。
- `processBlockBypassed()`は無遅延Pass-throughをせず、通常`processBlock()`と共通のRaw Dry 5 ms Delay/Crossfade処理を呼ぶ。
- Cakewalk Host Bypass、Plugin内Bypass、Automationのどの入口でも単一Parameter値を正とする。

## 4. Parameter変換

### 4.1 Drive

```text
driveLinear = 10^(driveDb / 20)
```

Driveは約20 msでSmoothする。

目的はAutomationやノブ操作時のクリック低減である。

### 4.2 Ceiling

```text
ceilingLinear = 10^(ceilingDb / 20)
```

Ceiling低下は安全側へ即時反映する。

Ceiling上昇でGainを急に戻さず、Gain EnvelopeのReleaseに従う。

### 4.3 Release

```text
releaseSeconds = releaseMs / 1000
releaseCoeff = exp(-1 / (releaseSeconds * sampleRate))
```

Releaseは1-pole復帰Envelopeの時定数`tau`である。`tau`経過後に最終値へ約63.2%接近し、残差が約36.8%になる。実装時は極端値をClampする。

## 5. Lookahead Delay

固定Lookahead:

```text
lookaheadSeconds = 0.005
lookaheadSamples = round(sampleRate * lookaheadSeconds)
```

例:

| Sample Rate | Delay Samples |
|---:|---:|
| 44.1 kHz | 約221 |
| 48 kHz | 240 |
| 88.2 kHz | 441 |
| 96 kHz | 480 |

`LookaheadBuffer`はRaw DryとDrive後Wetを別々に保持するCircular Bufferとし、`prepare()`で最大必要長を確保する。単一Frame構造へRaw/Wet両方を格納してもよいが、読み出し時に同一Sample Indexの2信号を独立して取得できなければならない。

Audio Thread中にresizeしない。

## 6. Limiter Peak検出

Drive適用後のサンプルを検出対象とする。

v1.0はStereo専用であり、左右の大きい方を検出する。

```text
peak[n] = max(abs(L[n]), abs(R[n]))
```

NaN/Infinityは0として扱い、内部へ伝播させない。

## 7. Lookahead Attack候補

選択アーキテクチャからFuture Peak WindowおよびMonotonic Dequeを削除する。5 ms Linear Lookahead Rampはv1.0最終仕様ではなく、PoCで評価する第一候補である。

Drive後の現在サンプルでPeakを検出した時点から、そのサンプルがWet Delayを抜ける`D = lookaheadSamples`後までにRequired Gainへ到達するLinear Gain RampをPoC第一候補とする。

```text
currentGain at detection
        |\
        | \
        |  \  D samples
        |   \
        |    requiredGain
```

Ramp中にさらに大きいPeakを検出した場合は、その検出時点のRamp Gainから新しいRequired Gainへ向けて、新たなDサンプルRampを設定する。複数Rampの競合では常に小さいGainを安全側として採用する。

Attack Rampは音質制御であり、Ceiling保証そのものはSection 10のSample Ceiling Safetyが担う。

PoC入力Pattern:

- 大Peakから小Peak
- 小Peakから大Peak
- 大小を交互に配置したPeak
- Peak間隔を1 sampleから`D = lookaheadSamples`までSweep
- Attack Ramp中にRelease復帰が競合するPattern

各PatternでCeiling超過、Ramp到達時点、Chattering、Safety Gain介入を記録し、Linear Rampを採用するか設計レビューで決定する。

## 8. Target Gain

```text
if detectedPeak <= ceilingLinear:
    targetGain = 1.0
else:
    targetGain = ceilingLinear / detectedPeak
```

Clamp:

```text
targetGain = clamp(targetGain, 0.0, 1.0)
```

## 9. Gain Envelope

v1.0では独立したAttackパラメータを持たない。PoC候補ではGainを下げる方向をSection 7の5 ms Linear Ramp、Gainが戻る方向をRelease時定数による1-poleで処理する。

```text
if attackRampRequiresMoreReduction:
    currentGain = min(activeLinearRampGain, currentGain)
else:
    currentGain = releaseCoeff * currentGain
                + (1 - releaseCoeff) * targetGain
```

意味:

- Downward: 5 ms Linear Lookahead Attack Ramp (PoC candidate)
- Upward: User ReleaseでSmooth

Wet音声を5 ms Delayしているため、検出Peakが出力へ到達するまでにRampを完了できる。

### 9.1 PoC評価ゲート

上記Linear RampをPoC第一候補として以下を確認する。

- 1から4 dB程度の通常GRで明確なクリックを生じない。
- Kick/Snareのアタックが不自然に欠落しない。
- 連続808で過度のChatteringを起こさない。

不合格の場合はAttack CurveまたはRamp再設定則を比較する。ただしSample Ceiling Safetyを外さず、設計書を更新してから本実装へ進む。

## 10. Sample Ceiling Safety

通常制御でCeilingを守るが、数値誤差・Parameter Automation等への最終保護としてSafety Gainを持つ。

Delayed SignalのPeak:

```text
delayedPeak = max(abs(delayedL), abs(delayedR))
```

```text
if delayedPeak <= ceilingLinear:
    safetyGain = 1.0
else:
    safetyGain = ceilingLinear / delayedPeak
```

最終Gain:

```text
appliedGain = min(currentGain, safetyGain)
```

出力:

```text
outL = delayedL * appliedGain
outR = delayedR * appliedGain
```

Hard Clipは置かない。

通常再生でSafety Gain介入が頻発する場合はLimiter制御設計不良と判定する。

PoCではBlockごとに少なくとも以下の診断値を非RT Test Harnessへ記録する。

- `safetyInterventionCount`: `safetyGain < currentGain`となったSample数
- `maxAdditionalSafetyGrDb`: Envelope Gainに対してSafety Gainが追加した最大GR
- Pattern名、Sample Rate、Block Size、Peak間隔

Safety GainはAttack候補の成否にかかわらず、100% Wet時のCeilingを最終保証する。

## 11. Gain Reduction Meter

Limiter内部から直接取得する。

```text
gainReductionDb = 20 * log10(max(appliedGain, epsilon))
```

内部では0 dB以下の値として保持し、`std::atomic<float>`相当の最新GR値へAudio Threadからstoreする。GUIは20から30 Hz程度でloadし、Meter FIFO、MeterWorker、`MeterSnapshot`を経由しない。UIの表示形式は`-3.2 dB`または`3.2 dB GR`のいずれかに統一する。

## 12. Ceiling Automation

Ceilingを急激に下げた場合は即時新CeilingをGain ComputerとSafety Gainへ反映する。

既にDelay Buffer内にあるサンプルもSafety GainでSample Ceilingを保証する。

Ceilingを上げた場合はReleaseによってGainが戻るため、急激なレベル上昇を避ける。

## 13. Bypass

BypassはLatencyを維持する。

Raw分岐はDrive適用前に行う。

Effect ON (100% Wet):

```text
Raw Input -> Drive -> Wet Delay -> Limiter -> Wet Output
```

Effect OFF:

```text
Raw Input -> Raw Delay -> Dry Output
```

Raw DelayとWet Delayは同一`lookaheadSamples`を使用する。Bypass切替時は5 ms程度のCrossfadeを行う。Dry側にはDrive、Gain Envelope、Safety Gainを適用しない。

Ceiling保証はCrossfade係数が100% Wetのときだけ適用する。100% DryおよびCrossfade中はRaw成分によるCeiling超過を正常動作として扱う。

APVTS `bypass`は`getBypassParameter()`から返し、VST3 `kIsBypass`とPlugin UIの共通Parameterとする。Hostが`processBlockBypassed()`を選択した場合もRaw Delayを進め、`lookaheadSamples`のLatencyを維持する。Host Bypassから復帰した際もDelay stateと時間位置を連続させる。

Crossfade Buffer/Stateは`prepare()`で準備し、Audio ThreadでAllocationしない。

## 14. Host Latency

```text
latencySamples = lookaheadSamples
```

`prepareToPlay()`等の初期化時にJUCEの`setLatencySamples()`を使ってホストへ通知する。

Bypass時も報告値を変更しない。

## 15. Tail

Delay内に残る最大約5 msの信号を考慮し、`getTailLengthSeconds()`は約0.005秒を返す方針とする。

DAW挙動確認で不要と判断した場合は、変更時に設計書とテストを更新する。

## 16. Audio Thread処理順

1サンプル/Blockの論理順序:

```text
read atomic parameter targets once at block start
    |
update smoothers
    |
validate input
    |
push raw input into dry delay buffer
    |
apply Drive
    |
push driven input into wet delay buffer
    |
update peak detector
    |
calculate required gain / update 5 ms linear attack ramp
    |
update gain envelope
    |
read aligned delayed dry and wet samples
    |
calculate safety gain
    |
apply min(envelope, safety) to wet only
    |
bypass crossfade if needed
    |
write output
    |
push final output to meter FIFO (non-blocking)
    |
publish current GR atomic directly to GUI
```

## 17. Audio Thread禁止事項

禁止:

- `new`, `delete`, `malloc`, `free`
- File I/O
- Network I/O
- Blocking mutex
- GUI access
- ValueTree `copyState()` / `replaceState()`
- libebur128 init/destroy
- high-volume log output

## 18. Parameter Access

JUCE APVTSのRaw Parameter ValueをAtomicとして読み取る。

v1.0はBlock Rate Automationとし、Drive / Ceiling / Release / Bypass targetを各Block先頭で1回だけ読む。Block内のSample Accurate eventは扱わない。Drive等のSmootherは読み取ったtargetへSample単位で追従してよい。

Audio ThreadでValueTree全体のコピー/置換を行わない。

## 19. MeterFIFO

Audio ThreadとMeter Worker間にSPSC Ring Bufferを置く。

データはFinal Outputを使用する。

基本形式（v1.0 Stereo専用）:

```cpp
struct MeterFrame
{
    float l;
    float r;
    std::uint64_t generation;
};
```

推奨初期容量:

```text
262144 frames
```

96 kHzでも約2.7秒分を保持できる。

容量は実測CPU負荷を見てPoC時に確定する。

## 20. Meter FIFO Overflow

FIFO Full時にAudio Threadは待たない。

処理:

1. Audio outputは継続。
2. そのMeterデータはdrop可。
3. `meterOverrun = true`をAtomicで設定。
4. LUFS-I / S / M / True PeakをINVALIDにする。
5. UIはReset要求を表示する。

Integrated Loudnessは途中Sample欠落後の継続値を正しい値として扱わない。

Overrun後もDSPから直接公開されるGain Reductionは有効であり、Meter Session Resetを受理するまでMeter各項目だけをINVALIDに保持する。

## 21. MeterWorker

Meter専用Worker Threadを持つ。

責務:

- FIFOをDrain
- libebur128へSample投入
- 10 Hz程度でM/S/Iを取得し、True Peak Session最大値を更新
- MeterSnapshotを更新
- Reset要求処理

Audio Threadより低い優先度で動作させる。

### 21.1 Lifecycle / Reprepare

`MeterWorker`は`PluginProcessor`が所有し、Editor有無に依存しない。

`prepareToPlay()`再実行時の順序:

```text
meterFifoWriteEnabled = false
    |
request Worker stop
    |
stop/join Worker outside Audio Thread
    |
clear FIFO
    |
++meterResetGeneration
    |
destroy/reinitialize libebur128 for new format
    |
publish all-unmeasured Snapshot with new generation
    |
start Worker
    |
meterFifoWriteEnabled = true
```

規則:

- 書込み停止中もAudio DSPと出力は継続し、Meter Frameだけをdropする。
- `releaseResources()`は書込み停止後にWorkerをstop/joinし、FIFOをclearする。
- `PluginProcessor` DestructorはWorkerが停止済みでも安全なidempotent stop/joinを必ず行う。
- Editor Open/CloseはWorkerを停止、再生成、Resetしない。
- Worker joinをAudio callbackから行わない。

## 22. libebur128設定

Version: 1.2.6固定。PoCのMode候補は次の完全な組合せとする。

```cpp
EBUR128_MODE_S
| EBUR128_MODE_I
| EBUR128_MODE_TRUE_PEAK
| EBUR128_MODE_HISTOGRAM
```

利用モードは、少なくとも以下を取得できる構成とする。

- Momentary
- Short-term
- Integrated
- True Peak

Sample Rate / Channel Count変更時はAudio処理停止/PrepareフェーズでMeter stateを再初期化する。

Audio callback中にdestroy/reinitしない。

Histogram Modeの精度は`04_test_plan.md`のEBU Test Set v5.0代表ケースで検証する。Histogramを有効にした長時間HarnessでWarm-up後のHeap/RSSが履歴時間に比例して増加しないこともPoC Gateとする。

API処理規則:

- `ebur128_loudness_momentary()` / `ebur128_loudness_shortterm()` / `ebur128_loudness_global()` / `ebur128_true_peak()`の戻り値を個別確認する。
- API Errorは該当Fieldだけ`error`へ遷移させる。
- API成功時にlibebur128が無音を示す`-Inf`を返した場合はStatusを`silence`とし、数値AtomicへstoreせずUIで`-∞`を表示する。
- NaN、`+Inf`、または無音条件に一致しない非有限ResultはStatusを`error`とし、数値Atomicへstoreしない。
- True Peakは`ebur128_true_peak(state, 0, &left)`と`ebur128_true_peak(state, 1, &right)`を使用し、Linear値の`max(left, right)`をSession最大へ反映する。
- Session最大True Peakに`ebur128_prev_true_peak()`は使用しない。

libebur128は`BUILD_SHARED_LIBS=OFF`で静的LibraryとしてPluginへLinkする。実行時DLLへの依存を作らない。

## 23. Meter Session Reset

Resetは明示操作のみ。

Resetしないイベント:

- Play
- Stop
- Pause
- Seek
- Loop

Reset対象はlibebur128のIntegrated/Short-term/Momentary履歴、全Meter項目のValidity、Session True Peak最大値、Overrun状態である。ResetはGenerationで順序付ける。

Reset処理:

```text
UI Reset button
   |
atomic meterResetGeneration++
   |
Audio Thread reads generation at block start
   |
tag each MeterFrame with generation
   |
MeterWorker drains FIFO in order
   |
on generation > meterAppliedGeneration:
  reinitialize libebur128 state
  clear True Peak maximum / validity / overrun
  set meterAppliedGeneration
    |
process first frame of the new generation
    |
publish Snapshot with meterAppliedGeneration
```

Workerは`frame.generation < meterAppliedGeneration`の古いFrameを破棄する。GUIは`meterResetGeneration`より古いSnapshotを表示しない。これによりReset前のFIFOまたはSnapshotがReset後のSessionへ混入しない。

Reset UIは`meterResetGeneration`を増加させた直後、Workerの稼働状態に依存せず`MeterSnapshotPublisher`から新Generationの全Field `unmeasured` Snapshotを即時公開し、WorkerをWakeする。Worker稼働中はlibebur128を再初期化し、停止中はReset適用をPendingとして保持して次回起動時に再初期化する。次回再生時は新GenerationのFrameだけを投入する。連続ResetはGeneration差で検出し、Boolean Requestのように取りこぼさない。

DAW project再ロード時は新規Meter状態から開始する。

## 24. MeterSnapshot

```cpp
enum class MeterFieldStatus : std::uint8_t
{
    unmeasured,
    valid,
    silence,
    error
};

struct MeterSnapshot
{
    float lufsIntegrated;
    float lufsShortTerm;
    float lufsMomentary;
    float truePeakDbTP;

    MeterFieldStatus lufsIntegratedStatus;
    MeterFieldStatus lufsShortTermStatus;
    MeterFieldStatus lufsMomentaryStatus;
    MeterFieldStatus truePeakStatus;
    bool meterOverrun;
    std::uint64_t generation;
};
```

公開Storageは上記各Fieldに対応するAtomicと`std::atomic<std::uint64_t> sequence`を持つ。単一の非Atomic structを同時Read/Writeしない。Worker、Reset UI、Lifecycle Threadという複数Writerは非RT Writer Mutexで直列化する。Audio ThreadはこのMutexにもSnapshot Publisherにも入らない。

Writer（Writer Mutex保持中）:

```text
sequence.fetch_add(1, acq_rel)   // even -> odd, write in progress
store every value/status/overrun/generation atomic (relaxed)
sequence.fetch_add(1, release)   // odd -> even, publish complete snapshot
```

Reader:

```text
loop:
  before = sequence.load(acquire)
  if before is odd: retry
  load every field atomic (relaxed) into local MeterSnapshot
  after = sequence.load(acquire)
  accept only if before == after and after is even
```

GUI Timerは有限回Retry後に前回の正常Snapshotを維持してよい。Torn Snapshotを表示するより更新を1回見送る。

True Peakは`ebur128_true_peak()`で得たL/Rの最大Linear値をSession最大値と比較し、Reset後から現在までの最大値をdBTPで公開する。Reset直後は`truePeakStatus=unmeasured`とし、最初の有効Sampleを処理後に`valid`とする。

Status表示:

| Status | UI | 数値Field |
|---|---|---|
| `unmeasured` | `--` | 無視 |
| `valid` | 有限数値 | 必ずFinite |
| `silence` | `-∞` | 無視 |
| `error` | `ERR` | 無視 |

非有限floatはどのStatusでも公開数値へstoreしない。

Gain Reductionは`MeterSnapshot`に含めない。GUIはDSPのAtomic GR値を別に取得する。

GUIはAudio Rateで更新しない。

描画更新目安: 20から30 Hz。

Meter数値計算は10 Hz程度で十分とする。

## 25. Streaming Referenceデータ

```cpp
enum class ReferenceClass
{
    official,
    industryReference,
    unavailable
};

struct StreamingReference
{
    std::string id;
    std::string displayName;
    std::optional<float> integratedLufs;
    ReferenceClass referenceClass;
    std::string note;
};
```

初期データ:

```text
Spotify
  value: -14
  class: official

Apple Music
  value: -16 approximately
  class: industryReference
  note: Sound Check exists; fixed LUFS not published in referenced Apple primary source

Amazon Music
  value: -14 approximately
  class: industryReference
  note: Loudness Normalization exists; fixed LUFS not published in referenced Amazon primary source
```

将来Reference値を変更する場合はDSPコードに埋め込まず、UI/metadata領域で変更可能にする。

## 26. Main UI詳細

ウィンドウはResizableを必須としない。まず固定基準サイズで実装する。

推奨初期サイズ:

```text
760 x 520 px程度
```

論理ブロック:

1. Header: Plugin name / Bypass
2. Main Loudness: LUFS-I / Reset
3. Loudness Scale: Streaming refs / Hip-Hop Guide / Current marker
4. Meter row: LUFS-M / LUFS-S / TP / GR
5. Control row: Drive / Ceiling / Release
6. Reference selector

### 26.1 表示単位

- Drive: dB
- Ceiling: dBFS
- True Peak: dBTP
- Release: ms
- LUFS: LUFS
- GR: dB

### 26.2 Meter表示状態

項目別Status:

```text
unmeasured -> --
valid      -> finite numeric value
silence    -> -∞
error      -> ERR
```

Overflow時:

```text
INVALID
METER OVERRUN - RESET
```

LUFS-M、LUFS-S、LUFS-I、True Peakを項目別Statusで描画し、1項目が未確定またはErrorでも他の有効項目を隠さない。

## 27. Host Bus仕様

Accept:

- Stereo input / Stereo output

Reject:

- Mono -> Stereo
- Stereo -> Mono
- Mono -> Mono
- Surround / Ambisonics

## 28. Processing Precision

v1.0:

- float32 supported
- float64 unsupported

ホストへ64-bit processing非対応を正しく通知する。

## 29. Block Size

Block Sizeを固定値として保存・仮定しない。

`numSamples == 0`でもCrashしない。

検証対象Block Size例:

- 1
- 17
- 64
- 128
- 256
- 511
- 512
- 1024

同一音源を異なるBlock分割で処理したとき、許容誤差内で同一結果になること。

## 30. Offline Rendering

Non-RealtimeでもMaximizerDSPは同一処理を行う。

OfflineではホストがRealtimeより高速にcallbackを呼ぶ可能性がある。

Meter Workerが追従できない場合:

- Meter INVALIDは許容
- Audio outputは正しく生成する

MeterのためにOffline Exportを遅延させない。

## 31. 異常値処理

### 31.1 Input NaN / Infinity

該当Sampleを0へ置換し、処理継続。

### 31.2 Internal Gain非有限

`currentGain = 1.0`へ安全復旧し、Debug buildでは診断フラグを立てる。

### 31.3 Meter異常

音声処理へ影響させない。

## 32. State保存

APVTS等を使用して次を保存する。

- Drive
- Ceiling
- Release
- Bypass
- Reference Platform
- `schemaVersion`

保存処理はAudio Thread外で行う。

State Rootへ整数`schemaVersion`を保存し、v1.0のCurrent Schemaを1とする。

Load規則:

1. `schemaVersion == 1`: 通常Restore。
2. VersionなしのPrototype State: Schema 0として既知FieldをMigrationし、不足FieldへDefaultを使用。
3. 未知の新Version: 不明Fieldを推測せずDefault StateへFallbackし、非RT診断フラグを残す。
4. Meter値、`meterResetGeneration`、`meterAppliedGeneration`、GR値は保存しない。

## 33. Build / Dependency固定

```text
C++: C++20
JUCE: 9.0.0
libebur128: 1.2.6
CMake: >= 4.2
Compiler: MSVC / Visual Studio 2026
Target: Windows 11 x64
Plugin: VST3 x64
```

Plugin identity:

```text
PLUGIN_NAME / PRODUCT_NAME: HipHopMax
COMPANY_NAME / Vendor: HipHopMax Audio
VST3_CATEGORIES: Fx Mastering
PLUGIN_MANUFACTURER_CODE: Hhpa
PLUGIN_CODE: Hmax
```

`Hhpa`と`Hmax`は先頭のみUppercaseの4文字固定IDとし、公開Build後は互換性Migrationなしに変更しない。

DependenciesはGit tagまたは明示Versionで固定する。

libebur128 1.2.6のTop-level CMakeはPolicy Version 2.8.12を宣言し、CMake 4.xでは互換性エラーになるため、以下をBuild PoCで決着する。

```text
HipHopMax top-level:
  cmake_minimum_required(VERSION 4.2)

Before adding libebur128 only:
  BUILD_SHARED_LIBS=OFF
  CMAKE_POLICY_VERSION_MINIMUM=3.5

Then:
  clean configure x64
  build Debug and Release
  verify static link and no libebur128 runtime DLL
```

`CMAKE_POLICY_VERSION_MINIMUM`はThird-party互換のため依存追加Scopeに限定し、HipHopMax自身のPolicy Versionを下げる目的では使用しない。PoCが失敗した場合は依存CMakeListsへの最小Patchを別候補として評価し、採用前に文書化する。

## 34. 実装順序

1. Empty VST3をCakewalkで認識
2. Pure C++ `MaximizerDSP` test harness
3. Drive
4. Raw/Wet Lookahead Buffer
5. 5 ms Linear Attack Ramp PoC
6. Gain Envelope / Ceiling Safety
7. Release
8. Stereo Link
9. APVTS/VST3 Host Bypass / Latency
10. DSP unit tests
11. libebur128 integration
12. Meter FIFO / Worker lifecycle
13. Atomic Fields + Sequence Counter Snapshot
14. LUFS / Histogram / True Peak integration tests
15. GUI
16. State save/restore
17. VST3 Validator
18. Cakewalk Sonar integration
19. Hip-Hop listening test

## 35. PoC完了条件

VST3 GUIを本格実装する前に、DSP PoCは以下を満たすこと。

- Sample Ceilingを守る。
- Ceiling保証が100% Wetに限定され、Dry/Crossfade試験と区別される。
- Latencyが計算値と一致する。
- Raw Dry経路にDrive/Limiterが混入しない。
- 5 ms Linear Attack Ramp候補を大→小、小→大、交互Peak、1～D Sample間隔、Release競合で評価する。
- Safety Gain介入回数と最大追加GRを記録し、100% Wet Ceilingを最終保証する。
- 1から4 dB程度のGRで明確なクリックがない。
- Releaseが1-pole時定数の定義どおり復帰する。
- Variable Block Sizeで結果が破綻しない。
- Stereo -> Stereoの出力が正しく、Mono BusをRejectする。
- NaN/InfinityでCrashしない。
- Audio ThreadでAllocationまたはBlocking Lockが発生しない。
- Denormal入力で異常なCPU増大を起こさない。
- Meter Session ResetのGeneration順序、項目別Validity、Overrun INVALIDが成立する。
- 停止中ResetでもSnapshotが直ちに`unmeasured`となり、旧Generation Frameが混入しない。
- Histogram ModeでEBU代表M/S/I/True Peak試験を通り、Warm-up後のMemoryが履歴時間に比例して増加しない。
- Atomic Fields + Sequence Counterの競合試験でTorn Snapshotを公開しない。
- Worker stop/reprepare/release/destroyを反復してThread leak、Use-after-free、Deadlockがない。
- Cakewalk Host BypassとPlugin内Bypassが同一Parameterを操作し、`processBlockBypassed()`でも5 ms Latencyを維持する。
- CMake 4.2以上でlibebur128 1.2.6を静的Buildできる。

## 36. 参考資料

- JUCE AudioProcessor: https://docs.juce.com/master/classjuce_1_1AudioProcessor.html
- JUCE CMake API: https://github.com/juce-framework/JUCE/blob/master/docs/CMake%20API.md
- JUCE APVTS: https://docs.juce.com/master/classjuce_1_1AudioProcessorValueTreeState.html
- JUCE SmoothedValue: https://docs.juce.com/master/classjuce_1_1SmoothedValue.html
- JUCE CMake: https://github.com/juce-framework/JUCE/blob/master/examples/CMake/AudioPlugin/CMakeLists.txt
- CMake `cmake_minimum_required`: https://cmake.org/cmake/help/latest/command/cmake_minimum_required.html
- CMake `CMAKE_POLICY_VERSION_MINIMUM`: https://cmake.org/cmake/help/latest/variable/CMAKE_POLICY_VERSION_MINIMUM.html
- libebur128: https://github.com/jiixyj/libebur128
- EBU Loudness: https://tech.ebu.ch/loudness/
- EBU Loudness Test Set: https://tech.ebu.ch/publications/ebu_loudness_test_set
