# HipHopMax テスト仕様書

- 文書版数: v0.3
- 作成日: 2026-08-15
- 対象: HipHopMax v1.0
- 前提: `01_requirements.md`, `02_basic_design.md`, `03_detailed_design.md`

## 1. 目的

本書は、HipHopMax v1.0が要件定義・基本設計・詳細設計を満たしていることを検証するための試験項目と合格条件を定義する。

テストは次の層に分ける。

1. DSP Unit Test
2. Meter Unit/Integration Test
3. Plugin Integration Test
4. VST3 Conformance Test
5. Cakewalk Sonar System Test
6. Listening Test

## 2. テスト環境

| 項目 | 基準環境 |
|---|---|
| OS | Windows 11 x64 |
| Compiler | MSVC / Visual Studio 2026 |
| Framework | JUCE 9.0.0 |
| Meter | libebur128 1.2.6 |
| Meter Mode候補 | `S | I | TRUE_PEAK | HISTOGRAM` |
| Build | CMake 4.2以上 / libebur128 static |
| Host | Cakewalk Sonar |
| Plugin | VST3 x64 |

検証Sample Rate:

- 44.1 kHz
- 48 kHz
- 88.2 kHz
- 96 kHz

代表Block Size:

- 1
- 17
- 64
- 128
- 256
- 511
- 512
- 1024

### 2.1 EBU Loudness Test Set v5.0代表ケース

取得元Archive:

- File: `ebu-loudness-test-setv05.zip`
- Size: 91,631,421 bytes
- SHA-256: `9cc500b4df83f7c21855c74dce795ef5209a752bf884253ae57d0ce512efb062`
- Official URL: `https://tech.ebu.ch/files/live/sites/tech/files/shared/testmaterial/ebu-loudness-test-setv05.zip`

PoC必須代表ケースは以下9ファイルとし、v5.0収録の全70 Audio FileはPoC必須にしない。取得時はArchiveおよび各FileのSHA-256を検証する。

| Test | 対象ファイル名 | 期待値 | 許容差 | SHA-256 |
|---|---|---|---|---|
| 3341-1 | `seq-3341-1-16bit.wav` | M/S/I = -23.0 LUFS | ±0.1 LU | `50dff94bf941756cae2b3e1a67931a28e1605a876be1b7f3707f2ffcf485955c` |
| 3341-2 | `seq-3341-2-16bit.wav` | M/S/I = -33.0 LUFS | ±0.1 LU | `7c9399188aed09980c76a67adc36b33f2d61c3e0c8457d83f7bf879122534ce3` |
| 3341-3 | `seq-3341-3-16bit-v02.wav` | I = -23.0 LUFS | ±0.1 LU | `1fced6ac2397d4337257185908bd49bb18314f441a0daf0ef602f671254d9efb` |
| 3341-5 | `seq-3341-5-16bit-v02.wav` | I = -23.0 LUFS | ±0.1 LU | `7acd129ddebb1935980585742075ad4aa82abe1147f7ce8abd28346b49561e61` |
| 3341-9 | `seq-3341-9-24bit.wav` | S = -23.0 LUFS、3秒後一定 | ±0.1 LU | `8157f3e794eac577b6007586fe47f51db8bfdc636729e6a773928c72791ef0f2` |
| 3341-12 | `seq-3341-12-24bit.wav` | M = -23.0 LUFS、1秒後一定 | ±0.1 LU | `d7fc88e31003553bb1329e84a8921217ae7c58045dfd620bc6b530d399a6933b` |
| 3341-15 | `seq-3341-15-24bit.wav.wav` | Max TP = -6.0 dBTP | +0.2 / -0.4 dB | `86f8a70eca1bc77623dd20a4acbadbc449711cd348615d852e3b0689827bea55` |
| 3341-19 | `seq-3341-19-24bit.wav.wav` | Max TP = +3.0 dBTP | +0.2 / -0.4 dB | `31bda52dc66bb63aa0d04b2e5a1e6a4332296fa5635570469b2863d05a06d309` |
| 3341-20 | `seq-3341-20-24bit.wav.wav` | Max TP = 0.0 dBTP | +0.2 / -0.4 dB | `a2df8791198781579a25e0437008ce9b1a48b13605a8b71886a360c4ceed16e4` |

ファイル15/19/20の`.wav.wav`はv5.0 Archive内の実ファイル名どおりである。Test前に各測定SessionをResetする。

## 3. DSP Unit Test

### DSP-001 Drive Gain

入力:

- 1 kHz sine
- 十分低いレベルでLimiterが作動しない状態

手順:

1. Drive 0 dBでRMS/Peakを測定。
2. Drive +6 dBに変更。
3. 出力差を測定。

合格:

- 期待Gainとの差が±0.01 dB以内。

### DSP-002 Drive Smoothing

手順:

- DriveをAutomationで0 dBから+12 dBへStep変更。

合格:

- Sample 1個でGainが不連続ジャンプしない。
- 約20 msの設計スムージングが確認できる。
- NaN/Infなし。

### DSP-003 Lookahead Latency

入力:

- 単一Impulse

合格:

- 出力Impulse位置が`round(sampleRate * 0.005)` Samples遅延。
- Host報告Latencyと一致。

### DSP-004 Ceiling Basic

入力:

- Ceilingを超えるSine/Impulse

条件:

- Ceiling -1 dBFS

合格:

- 100% Wetの全出力SampleがCeiling + 0.01 dB以内。
- Hard Clipによる平坦化を意図的に行っていない。

### DSP-005 Ceiling Automation Down

手順:

- 強い定常信号中にCeilingを-0.1 dBFSから-3 dBFSへ急変。

合格:

- 新Ceiling反映後、Safety制御を含めSample Peakが新Ceilingを超えない。
- Crash/NaNなし。

### DSP-006 Release

入力:

- Impulse burstまたは短い高レベルTone

条件:

- Release 50 / 100 / 500 / 1000 ms

合格:

- Gain Envelopeが`exp(-1 / (tau * sampleRate))`の1-pole曲線へ許容誤差内で一致する。
- 各設定の`tau`経過時点で最終値へ約63.2%接近し、残差が約36.8%である。
- 長いReleaseほど復帰が遅い。

### DSP-007 Stereo Link

入力:

- Lのみ大Peak
- Rは低レベルTone

合格:

- L/Rへ同一`appliedGain`が適用される。
- Stereo imageが左右別Gainで揺れない。

### DSP-008 Stereo-only Contract

手順:

- Stereo -> Stereoを構成する。
- Mono -> Monoおよび変換Busを要求する。

合格:

- Stereo -> StereoのみAcceptする。
- Mono -> Mono、Mono -> Stereo、Stereo -> MonoをRejectする。

### DSP-009 Variable Block Invariance

同一WAVを異なるBlock分割で処理する。

合格:

- 1, 17, 64, 128, 511, 1024 Sample Blockで出力が浮動小数点許容誤差内で一致。
- Attack Ramp / Raw-Wet Delay stateがBlock境界で破綻しない。

### DSP-010 Sample Rate

44.1 / 48 / 88.2 / 96 kHzで同一論理試験を実施。

合格:

- Lookahead時間が約5 ms。
- Crashなし。
- Ceiling保証。

### DSP-011 Zero Sample Block

`numSamples == 0`相当処理。

合格:

- Crashなし。
- State破壊なし。

### DSP-012 NaN / Infinity

入力BufferへNaN/Infinityを注入。

合格:

- Plugin全体へ非有限値を伝播させない。
- Crashなし。

### DSP-013 Bypass Latency

ImpulseをON/OFFで比較。

合格:

- Bypass ON/OFFで出力Sample位置が一致。
- Raw DelayとWet Delayが同じ5 ms位置に一致。

### DSP-014 Bypass Click

Tone再生中にBypassを繰り返し切替。

合格:

- Crossfadeが機能し、明確な単Sampleクリックが発生しない。

### DSP-015 Linear Lookahead Attack Ramp

入力:

- 既知Sample位置にCeiling超過Impulse
- Ramp途中にさらに大きいImpulseを置いた信号

合格:

- Peak検出から`lookaheadSamples`後までにRequired Gainへ到達する。
- 途中の大Peakで、現在Ramp Gainから新Required Gainへ安全側に再設定される。
- Monotonic DequeまたはFuture Peak Windowへ依存しない。

### DSP-016 Raw Dry分離

手順:

1. Driveを+12 dBに設定。
2. 100% Bypass Dryを出力。
3. 同LatencyのRaw入力とNull/Level比較する。

合格:

- Dry出力へDrive、Limiter Gain、Safety Gainが混入しない。
- Raw入力との差は5 ms Delay以外、浮動小数点許容誤差内。

### DSP-017 Ceiling保証範囲

入力:

- Ceilingを超えるRaw信号

合格:

- 100% WetではCeilingを保証する。
- 100% DryおよびCrossfade中のCeiling超過をFailureと判定しない。
- 状態遷移中もCrash/NaNを起こさない。

### DSP-018 Block-rate Automation

手順:

- Drive / Ceiling / Release / Bypass targetをBlock境界およびBlock途中に変更するHost Harnessを使用。

合格:

- targetは次のBlock先頭で1回取得される。
- 同一Block内でSample Accurate変更を行わない。
- Drive等の設計済みSmoothingは維持される。

### DSP-019 Real-time Allocation

手順:

- `prepare()`後のAudio callbackでHeap Allocation/Freeを計測またはHookする。

合格:

- 通常、Bypass切替、Automation、FIFO Fullの各経路でAllocation/Freeが0回。

### DSP-020 Real-time Lock

手順:

- Audio callback内のMutex/Condition Variable取得を計測または静的・動的検査する。

合格:

- Blocking Lockおよび待機が0回。

### DSP-021 Denormal

入力:

- 無音に近いSubnormal信号と長いRelease tail

合格:

- 非Denormal対策が有効で、通常無音時と比べ異常なCPU増大がない。
- 出力にNaN/Infinityがない。

### DSP-022 Attack Ramp Stress / Safety診断

入力Pattern:

- 大Peak -> 小Peak
- 小Peak -> 大Peak
- 大小交互Peak
- Peak間隔1 sampleから`D = lookaheadSamples`までのSweep
- Attack RampとRelease復帰の競合

合格:

- 100% Wet SampleがCeiling + 0.01 dBを超えない。
- 各PatternでRamp到達とChattering有無を記録する。
- `safetyInterventionCount`と`maxAdditionalSafetyGrDb`をPattern/Sample Rate/Block Size別に記録する。
- 結果を設計レビューし、5 ms Linear Rampを採用するか再設計する。PoC前にv1.0最終仕様として固定しない。

## 4. Meter Test

### MTR-001 LUFS-M

`seq-3341-1-16bit.wav`、`seq-3341-2-16bit.wav`、`seq-3341-12-24bit.wav`を使用する。

合格:

- Section 2.1の期待値/許容差をHistogram Mode有効状態で満たす。

### MTR-002 LUFS-S

`seq-3341-1-16bit.wav`、`seq-3341-2-16bit.wav`、`seq-3341-9-24bit.wav`を使用する。

合格:

- Section 2.1の期待値/許容差をHistogram Mode有効状態で満たす。

### MTR-003 LUFS-I

`seq-3341-1-16bit.wav`、`seq-3341-2-16bit.wav`、`seq-3341-3-16bit-v02.wav`、`seq-3341-5-16bit-v02.wav`を使用する。

合格:

- Section 2.1の期待値/許容差をHistogram Mode有効状態で満たす。

### MTR-004 True Peak

`seq-3341-15-24bit.wav.wav`、`seq-3341-19-24bit.wav.wav`、`seq-3341-20-24bit.wav.wav`を使用する。

合格:

- `ebur128_true_peak()`でL/Rを取得し、その最大値がSection 2.1の非対称許容差内。
- Session最大値が途中の低いTrue Peakで低下しない。

### MTR-005 Meter Session Reset

手順:

1. Audio Aを再生。
2. Stop。
3. 再生再開。
4. ResetせずLUFS-IとTrue Peak最大値の継続を確認。
5. Reset押下。
6. Audio Bを再生。

合格:

- Play/Stopでは自動Resetしない。
- Reset操作でLUFS-I/S/M履歴、各Validity、True Peak最大値、Overrun状態が消える。
- 新Sampleの必要量に達した項目から個別にValidへ戻る。

### MTR-006 Loop Semantics

Loop再生を複数回実行。

合格:

- Resetしない限りLoop分もIntegratedへ蓄積する。

### MTR-007 FIFO Overrun

Meter Workerを意図的に遅延させFIFOをOverflowさせる。

合格:

- Audio ThreadがBlockしない。
- Audio DropoutをMeter側待ちが原因で起こさない。
- MeterはINVALIDへ遷移。
- UIにReset要求を表示できる。

### MTR-008 Meter Recovery

MTR-007後にReset。

合格:

- 新しい測定として復帰できる。

### MTR-009 True Peak Session Maximum

手順:

1. True Peak Aを測定。
2. より低い信号を測定。
3. より高いTrue Peak Bを測定。
4. Reset後に低い信号Cを測定。

合格:

- Reset前はAを下回らず、B測定後はBを示す。
- Reset直後はTrue Peak Invalid。
- Reset後は過去のBでなく、Reset後から現在までのCを最大値として示す。

### MTR-010 Per-field Validity

手順:

- Reset直後から、M/S/I/True Peakが有効化されるまでSnapshotを観測する。

合格:

- M/S/I/True Peakの`MeterFieldStatus`が独立する。
- 1項目が`unmeasured`または`error`でも他の`valid`項目をGUIが表示できる。

### MTR-011 Reset Generation Ordering

手順:

- FIFOに旧Generation Frameを残した状態でResetを連続2回要求し、新Generation Frameと古いSnapshotを意図的に競合させる。
- Transport停止中でFrameが到着しない状態でもResetする。

合格:

- WorkerはGeneration上昇をFIFO順に適用し、各Generation最初のFrame前にSessionを初期化する。
- `meterAppliedGeneration`より古いFrameを破棄する。
- GUIは`meterResetGeneration`より古いSnapshotを表示しない。
- Boolean Requestの取りこぼしに相当するReset欠落がない。
- 停止中Reset直後に全Fieldが`unmeasured`となり、再生再開後は新Generation Frameだけを測定する。
- Worker停止済みの場合もUI Reset pathがSnapshotを即時`unmeasured`として公開し、次回Worker起動時にlibebur128 Resetを適用する。

### MTR-012 GR Direct Path

手順:

- MeterWorkerを停止または意図的にOverrunさせながらLimiterを動作させる。

合格:

- LUFS/True PeakはINVALIDになってもAudioは継続する。
- Gain ReductionはDSP Atomic経路から更新され続ける。
- `MeterSnapshot`にGRを含めない。

### MTR-013 Histogram Accuracy

条件:

```text
EBUR128_MODE_S
| EBUR128_MODE_I
| EBUR128_MODE_TRUE_PEAK
| EBUR128_MODE_HISTOGRAM
```

手順:

- Section 2.1の3341-1/2/3/5/9/12を新Sessionごとに測定する。

合格:

- MTR-001～003の期待値と±0.1 LU許容差をすべて満たす。
- HistogramなしのReference runとの差を記録し、許容差外の偏りがない。

### MTR-014 Histogram Long-run Memory

手順:

- 48 kHz Stereoの反復信号をOffline Harnessで24時間相当投入する。
- 最初の10分相当をWarm-upとし、その後10分間隔でMeter stateのLive Heap bytes、Allocation count、Process RSSを記録する。

合格:

- Warm-up後に履歴時間へ比例する単調なHeap増加がない。
- libebur128 Meter stateのLive Allocation countが一定。
- 最終Live Heap bytesがWarm-up基準 + 1 MiB以内。
- RSS変動だけで判定せず、Heap計測と合わせてMemory leakなしと確認する。

### MTR-015 Torn Snapshot Concurrency

手順:

- Worker/Reset/Lifecycleを模した複数Writerと複数Readerを動作させ、Writer MutexでPublishを直列化しつつ、各更新に相関するGeneration/値/Status Patternを1000万回以上Publish/Readする。
- Sequenceが奇数中の強制yieldとField間yieldを挿入する。

合格:

- Readerが採用したSnapshotは読取り前後Sequenceが同じ偶数。
- 異なるPattern世代のFieldが混ざったSnapshotが0件。
- Retry上限時は前回正常Snapshotを維持し、部分更新を表示しない。

### MTR-016 Worker Stop/Reprepare/Destroy

手順:

1. Meter処理中に`prepareToPlay()`再実行を100回反復。
2. Sample Rate / Block Sizeを交互に変更。
3. `releaseResources()`を反復。
4. Editorを開閉しながら再生。
5. Processorを破棄。

合格:

- FIFO write停止 -> Worker stop/join -> FIFO clear -> Generation更新 -> libebur128再初期化 -> Snapshot `unmeasured` -> Worker再開の順序をTraceで確認。
- `releaseResources()`とDestructor終了時にWorker Threadが残らない。
- Editor開閉だけではWorkerが停止またはResetされない。
- Deadlock、Use-after-free、旧Generation混入がない。
- Audio callbackからjoinまたはBlocking Lockを行わない。

### MTR-017 Silence / API Error Mapping

手順:

- Reset直後、十分な長さのDigital Silence、API Error注入、NaN/+Inf/-Inf result注入を項目別に試験する。

合格:

- 未測定=`--`、無音=`-∞`、Error=`ERR`。
- Errorは該当Fieldだけへ反映し、他FieldとAudioを停止しない。
- 非有限floatをSnapshot数値Atomicへstoreしない。

## 5. Streaming Reference UI Test

### UI-001 Hip-Hop Guide

合格:

- -9 to -7 LUFSをGUIDEとして表示。
- Official targetと誤認させる文言がない。

### UI-002 Spotify

合格:

- -14 LUFSをOfficial Referenceとして表示。

### UI-003 Apple Music

合格:

- 約-16 LUFSをIndustry Referenceとして表示。
- Official固定値と表示しない。

### UI-004 Amazon Music

合格:

- 約-14 LUFSをIndustry Referenceとして表示。
- Amazon公式のLoudness Normalization機能と、非公式固定値を区別する。

### UI-005 Unit Labels

合格:

- Ceiling: dBFS
- True Peak: dBTP
- LUFS: LUFS
- Release: ms
- GR: dB

### UI-006 Meter Status Labels

合格:

- `unmeasured`は`--`。
- `silence`は`-∞`。
- `error`は`ERR`。
- 1項目のStatusが他項目の表示を上書きしない。

## 6. Plugin Integration Test

### INT-001 Parameter Automation

CakewalkでDrive/Ceiling/Release/Bypass Automationを記録・再生。

合格:

- Block Rate仕様で値が正しく追従。
- Crash/NaNなし。

### INT-002 State Save/Restore

設定を変更してCakewalk Projectを保存、終了、再読込。

合格:

- Drive/Ceiling/Release/Bypass/Reference選択が復元。
- Meter履歴は新規状態。
- `schemaVersion=1`が保存される。
- VersionなしPrototype StateがSchema 0からMigrationされる。
- 未知の新Schemaで安全なDefaultへFallbackする。

### INT-003 Bus Support

合格:

- Stereo->Stereo Accept
- Mono->Mono Reject
- 不要なSurround BusをReject

### INT-004 Precision

ホストの64-bit Plugin Processing要求への応答を確認。

合格:

- v1.0は32-bit floatのみを正しく表明し、誤動作しない。

### INT-005 Offline Render

Cakewalk Export/Bounce/Freezeを実行。

合格:

- Audio DSPがRealtimeと同じ仕様で出力。
- Meter Workerが追従できなくてもExport Audioは正常。

### INT-006 CMake 4.2 Policy Compatibility

手順:

1. Clean Build Treeを作成。
2. CMake 4.2以上でTop-levelをConfigure。
3. libebur128追加Scopeに`CMAKE_POLICY_VERSION_MINIMUM=3.5`を適用。
4. Debug/Release x64をBuild。

合格:

- libebur128 1.2.6のPolicy Version 2.8.12によるConfigure Errorを回避する。
- HipHopMax自身はCMake 4.2 Policyを維持する。
- 使用したScopeと設定をBuild Logへ記録する。

### INT-007 libebur128 Static Link

合格:

- `BUILD_SHARED_LIBS=OFF`でBuildされる。
- Pluginがlibebur128のRuntime DLLを要求しない。
- Debug/Release双方でSymbol解決とMeter Testが成功する。

### INT-008 Cakewalk/VST3 Host Bypass

手順:

1. Plugin内Bypass Buttonを操作しCakewalk側表示/Parameter値を観測。
2. Cakewalk Host Bypassを操作しPlugin内Button/Parameter値を観測。
3. Host Bypass Automationを記録・再生。
4. ImpulseとToneで`processBlock()` / `processBlockBypassed()`を比較。

合格:

- `getBypassParameter()`がAPVTS `bypass`を返す。
- VST3 Parameterが`kIsBypass`として認識される。
- Cakewalk Host BypassとPlugin内Bypassが同じParameterを双方向に操作し、二重Bypass stateを持たない。
- `processBlockBypassed()`は無遅延Pass-throughをせず、Impulse位置が`round(sampleRate * 0.005)` Samples遅延する。
- Host報告LatencyはBypass前後で不変。
- Tone切替でDSP-014のClick基準を満たす。

### INT-009 Plugin Identity Metadata

合格:

- Plugin/Product Name: `HipHopMax`
- Vendor/Company: `HipHopMax Audio`
- VST3 Category: `Fx|Mastering`
- `PLUGIN_MANUFACTURER_CODE`: `Hhpa`
- `PLUGIN_CODE`: `Hmax`
- Cakewalk Scan結果とVST3 Validator情報が一致する。

## 7. VST3 Conformance Test

### VST-001 Steinberg Validator

Steinberg VST3 SDK Validatorで検証。

合格:

- Release対象BuildでValidatorに重大Failなし。

### VST-002 Plugin Scan

Cakewalk Plugin Scanを実行。

合格:

- Blacklist/Crashなし。
- `HipHopMax` / `HipHopMax Audio` / `Fx|Mastering`で認識。

## 8. Cakewalk Sonar System Test

### SYS-001 Insert/Remove

Master Busへ複数回Insert/Remove。

合格:

- Crashなし。

### SYS-002 Transport

Play/Stop/Pause/Seek/Loopを繰り返す。

合格:

- Audio state破綻なし。
- LUFS-I semanticsが仕様どおり。

### SYS-003 Project Reload

大きなProject内で保存/再読込。

合格:

- State restore正常。

### SYS-004 Long Run

1時間以上連続再生。

合格:

- Crash/NaN/Infinityなし。
- Memoryが継続増加しない。

## 9. Listening Test

Listening Testは数値試験の代用ではなく、DSP音質の最終確認として実施する。

### LST-001 Moderate Limiting

Hip-Hop完成Mixに対し、GR 1から4 dB程度。

確認:

- 明確なクリックなし
- Kick/SnareのAttackが不自然に消えない
- 808が周期的に不自然に揺れない

### LST-002 Heavy Limiting

意図的にGR 6 dB以上。

目的:

- 破綻点を把握する
- Crashではなく音質劣化として振る舞うことを確認する

### LST-003 Release Sweep

FastからSlowまで比較。

確認:

- Releaseノブの聴感変化が一貫する
- Fast側で歪み、Slow側でPumpが増える場合の傾向を記録する

## 10. PoC Gate

GUI本実装へ進む前に以下をすべてPASSする。

- DSP-001
- DSP-002
- DSP-003
- DSP-004
- DSP-005
- DSP-006
- DSP-007
- DSP-008
- DSP-009
- DSP-010
- DSP-011
- DSP-012
- DSP-013
- DSP-014
- DSP-015
- DSP-016
- DSP-017
- DSP-018
- DSP-019
- DSP-020
- DSP-021
- DSP-022
- MTR-001
- MTR-002
- MTR-003
- MTR-004
- MTR-005
- MTR-007
- MTR-008
- MTR-009
- MTR-010
- MTR-011
- MTR-012
- MTR-013
- MTR-014
- MTR-015
- MTR-016
- MTR-017
- INT-006
- INT-007
- INT-008
- INT-009
- LST-001

Fail時はGUI本実装へ進まず、該当するLimiter DSP、Meter ordering、またはBuild方針を設計書へ戻して修正する。

## 11. v1.0 Release Gate

Release候補は以下を満たすこと。

- 全PoC Gate PASS
- EBU Meter integration PASS
- State Save/Restore PASS
- State Schema migration/fallback PASS
- Audio Thread Allocation/Lock/Denormal PASS
- Histogram long-run bounded-memory PASS
- Host Bypass / Torn Snapshot / Worker Lifecycle PASS
- CMake 4.2+ / libebur128 static build PASS
- VST3 Validator PASS
- Cakewalk System Test PASS
- 1時間Long Run PASS
- 実曲Listening Testで致命的なArtifactなし

## 12. テスト結果記録フォーマット

各試験は次を記録する。

```text
Test ID:
Build/Commit:
Date:
Environment:
Sample Rate:
Block Size:
Input:
Expected:
Actual:
Result: PASS / FAIL / BLOCKED
Notes:
Evidence path:
```

## 13. 参考資料

- EBU Loudness Test Set v5.0: https://tech.ebu.ch/publications/ebu_loudness_test_set
- EBU Loudness / Tech 3341 overview: https://tech.ebu.ch/loudness/
- EBU Tech 3341 v4.0: https://tech.ebu.ch/files/live/sites/tech/files/shared/tech/tech3341v4_0.pdf
- JUCE AudioProcessor bypass API: https://docs.juce.com/master/classjuce_1_1AudioProcessor.html
- JUCE CMake API: https://github.com/juce-framework/JUCE/blob/master/docs/CMake%20API.md
- libebur128: https://github.com/jiixyj/libebur128
- JUCE AudioProcessor: https://docs.juce.com/master/classjuce_1_1AudioProcessor.html
- JUCE APVTS: https://docs.juce.com/master/classjuce_1_1AudioProcessorValueTreeState.html
- CMake `cmake_minimum_required`: https://cmake.org/cmake/help/latest/command/cmake_minimum_required.html
- CMake `CMAKE_POLICY_VERSION_MINIMUM`: https://cmake.org/cmake/help/latest/variable/CMAKE_POLICY_VERSION_MINIMUM.html
