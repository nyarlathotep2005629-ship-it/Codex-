# HipHopMax 個人使用版 v1.0.0 開発記録

- 確定日: 2026-08-16
- 対象: Windows x64 / VST3 / Release / static libebur128
- 判定: **個人使用版 v1.0.0として受入れ**
- 設計変更: なし
- DSP仕様変更: なし
- 版番号変更: CMake project versionを`0.1.0`から`1.0.0`へ更新

この判定は、ユーザー自身によるCakewalk Sonar上の手動受入れ結果を含む個人使用向けの区切りである。v0.3 `04_test_plan.md`の全Release Gateを形式的にPASSしたという意味ではなく、未実施・部分実施項目は本記録で明示する。

## 1. v1.0.0の実装状態

実装済み:

- Stereo-only、float32-only VST3
- 5 ms固定Lookaheadと固定Latency報告
- Drive 20 ms smoothing
- Ceiling、Release、Stereo-link Limiter
- Linear Attack Rampと最終Sample Ceiling Safety
- Wet/Raw Dry分離、Latency維持Bypass、Cakewalk Host Bypass連携
- APVTS State、`schemaVersion=1`、Schema 0 migration、未知Schema fallback
- libebur128 Histogram ModeによるLUFS-M/S/IおよびTrue Peakバックエンド
- Meter FIFO、Worker lifecycle、Reset Generation、整合Snapshot、Overrun状態
- JUCE Generic Editor

未実装:

- v0.3設計の専用GUI
- LUFS/True Peak/GR表示、Meter Reset、Status表示
- Streaming Referenceの数値・種別を表示する専用UI

## 2. 確定ビルド

Clean configure/build用ディレクトリ:

`build\v1.0-release-x64`

Build結果:

- CMake 4.2.1 configure: PASS
- Release x64全ターゲットbuild: PASS
- VST3 metadata version: `1.0.0`
- Vendor: `HipHopMax Audio`
- Category: `Fx|Mastering`
- CID: `ABCDEF019182FAEB48687061486D6178`
- Release module size: `6,391,296 bytes`
- Release module SHA-256: `5d047a213d5704f709c33f3a158a3d7f4ebf5413a145d28790ed4ce83b31db03`

## 3. 実施済み自動テスト

最終Release buildでCTestを再実行した。

| CTest | 結果 |
|---|---:|
| `INT007_libebur128_static_smoke` | PASS |
| `DSP_PoC_Gate` | PASS |
| `Meter_PoC_Gate` | PASS |
| `Plugin_Integration_PoC` | PASS |
| 合計 | **4/4 PASS** |

明確に自動確認できた範囲:

- DSP-001からDSP-017、DSP-022
- MTR-001からMTR-005、MTR-008、MTR-011、MTR-013からMTR-017
- INT-002のStateロジック、INT-003、INT-004、INT-006、INT-007、INT-009
- INT-005のRealtime/Non-realtime同一入力比較
- INT-008のBypass parameter、Latency、Raw Dry、`processBlockBypassed()`
- EBU test assetのLUFS/True Peak精度
- MTR-014 24時間相当Histogram bounded-memory試験（過去実行）
- MTR-015 10,000,000 publish相当のSnapshot concurrency（過去実行および最終build再実行）
- MTR-016 Worker start/stop/reprepare/editor lifecycle

DSP-022最終値:

- `safetyInterventionCount=20`
- `maxAdditionalSafetyGrDb=0.00244968`

MTR-014過去実行値:

- 24時間相当、144 checkpoints
- baseline heap `7,034,621 bytes`
- final heap `7,031,477 bytes`
- monotonic growthなし

## 4. Steinberg Validator / Cakewalk / 人間による確認

### Steinberg VST3 Validator

- Validator: VST 3.8.0
- Extensive test: **537 passed / 0 failed**
- Exit code: `0`
- 5 parametersを確認: DRIVE、CEILING、RELEASE、BYPASS、REFERENCE
- BYPASSはToggleおよびBypass parameterとして認識
- Stereo以外のBusを拒否
- 64-bit audio processingを未対応として正しく表明

### Cakewalk Sonar

自動操作を含む確認:

- VST再スキャン: 認識
- Category: `Mastering`
- 実トラックへのInsert: 成功
- 表示metadata: `HipHopMax Audio / VST3 / 64 Bit / In 2 / Out 2`
- Plugin内BypassとCakewalk Host Bypassの双方向反映: 確認

ユーザー自身の手動確認:

- VST3として正常にロード: PASS
- 音圧・音量が仕様どおり変化: PASS
- Ceiling / Peak調整: PASS
- 聴感上、不自然な音質変化なし: PASS
- Bypass: PASS
- 手動操作でクラッシュ・エラー再現なし: PASS
- LST-001相当の実曲聴感: PASS（ユーザー報告）

## 5. 未実施・未完了テスト

以下はPASS扱いにしない。

| ID/範囲 | 状態 | 未完了内容 |
|---|---|---|
| DSP-018 | 未完了 | 実装テストはBlock切替後の安全性のみ。設計手順全体のAutomation時系列検証は未完了 |
| DSP-019 | 未完了 | 通常経路のAllocation 0は確認。Bypass/Automation/FIFO Full全経路のAllocation/Free hookは未完了 |
| DSP-020 | 未完了 | DSP codeにblocking lockがないことを静的確認したのみ。動的lock計測は未実施 |
| DSP-021 | 未完了 | Subnormal除去とfinite outputは確認。長いRelease tailのCPU比較は未実施 |
| MTR-006 | 未実施 | Cakewalk Loop Semantics |
| MTR-007 | 未完了 | FIFO non-blocking/回復は確認。専用UIのINVALID/Reset要求表示は未実装 |
| MTR-009 | 未完了 | Resetと低い信号への切替は確認。設計書のA→低信号→B→Reset→C全系列は未完了 |
| MTR-010 | 未完了 | Field status独立性はバックエンドで確認。専用UIでの独立表示は未実装 |
| MTR-012 | 未完了 | テスト実行表示にはIDが含まれるが、Worker停止/Overrun中のGR Atomic継続を独立assertしていない |
| UI-001からUI-006 | 未実施 | 専用UI未実装 |
| INT-001 | 未実施 | CakewalkでのDrive/Ceiling/Release/Bypass Automation記録・再生 |
| INT-002 | 未完了 | Stateロジックは自動PASS。Cakewalk Project保存・終了・再読込は未実施 |
| INT-005 | 未完了 | 同一入力のRealtime/Non-realtime比較はPASS。Cakewalk Export/Bounce/Freezeは未実施 |
| INT-008 | 未完了 | 双方向BypassとDSP側条件は確認。Host Bypass Automation記録・再生は未実施 |
| SYS-001 | 未完了 | 通常Insert/使用は手動PASS。Master Busでの複数回Insert/Remove試験は未記録 |
| SYS-002 | 未実施 | Play/Stop/Pause/Seek/Loop反復 |
| SYS-003 | 未実施 | 大規模Projectの保存・再読込 |
| SYS-004 | 未実施 | Cakewalk実機で1時間以上の連続再生 |
| LST-002 | 未実施 | GR 6 dB以上のHeavy Limiting |
| LST-003 | 未実施 | Release Sweep |

したがって、v0.3設計書の形式的な全Release Gateは未完了である。ユーザー判断により、これらを個人使用版v1.0.0のBlocking条件とはしない。

## 6. 既知の問題

### Computer Use中のCakewalkクラッシュ

- 発生: 2026-08-15、Computer UseでHipHopMax editorを閉じた直後
- Cakewalk表示: `CA230100 (Pure Virtual Function Call)`
- Dump exception: `0xCA230000`、`VCRUNTIME140!purecall+0x14`
- Dump stack候補: UI Automation系が中心
- HipHopMax module固有のstack frame: 未確認
- ユーザー手動操作: 再現なし
- 原因: 未確定
- v1.0.0判定: 非Blocking。ただし再発時はdumpと操作手順を保存する

### その他

- 専用Meter UIがなく、v1.0.0はJUCE Generic Editorを使用する。
- Build時にJUCE/HarfBuzzのCP932 C4819、FIFO cache-line alignment C4324、JUCE旧Parameter constructor C4996警告が出る。Release buildとValidatorは成功している。

## 7. v1.1で検討できる事項

- UI-001からUI-006を満たす専用GUIとMeter/Reset表示
- INT-001、INT-008のCakewalk Automation実機試験
- SYS-001からSYS-004、1時間実機Long Run、Project reload
- DSP-019からDSP-021の動的RT instrumentation強化
- MTR-012のGR Direct Path独立テスト
- Computer Use/UI AutomationとCakewalkのクラッシュ再現性調査
- LST-002 Heavy Limiting、LST-003 Release Sweep

## 8. 最終成果物

配布用bundle:

`artifacts\HipHopMax-v1.0.0-win-x64\HipHopMax.vst3`

Cakewalkへ導入するのは、この`.vst3`ディレクトリ一式である。

