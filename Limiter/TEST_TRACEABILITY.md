# HipHopMax テスト対応表

更新日: 2026-10-21

## CTestターゲットとテストコード

| CTest名 | ソース | 対応ID / 範囲 | 既定実行 | 備考 |
|---|---|---|---|---|
| `INT007_libebur128_static_smoke` | `tests/LibEbur128Smoke.cpp` | INT-007 | Yes | libebur128静的リンクの最小Smoke |
| `DSP_PoC_Gate` | `tests/DspTests.cpp` | DSP-001〜DSP-022 | Yes | 1 executable内でIDごとに実行。DSP-019/020/021は専用動的検証範囲をRelease Recordで区別 |
| `Meter_PoC_Gate` | `tests/MeterTests.cpp`、`src/metering/*` | MTR-001〜005、007〜013、015〜017 | Yes | EBU代表音源を使用。MTR-006/MTR-014は別境界 |
| `Plugin_Integration_PoC` | `tests/PluginIntegrationTests.cpp`、関連実装 | INT-002〜005、008〜009、DSP-003/004/016、MTR-005/011/016 | Yes | Cakewalkを使わないPlugin統合テスト |
| `MTR014_meter_long_run` | `tests/MeterLongRun.cpp` | MTR-014 | Opt-in | `HIPHOPMAX_ENABLE_LONG_RUN_TESTS=ON` のときだけCTest登録。標準実行には含めない |

`04_test_plan.md` の設計IDを、テストコード内のエラー文・実行ラベル・CTest名から追跡できる。1つの実行ファイルが複数IDをまとめて検証する場合、個々のIDの実行結果はそのプロセス出力とソースの `run(...)` 対応で確認する。

## CMake / CTest手順

依存を配置したASCIIパスのプロジェクトルートで実行する。

```powershell
cmake --preset vs2026-x64-release
cmake --build --preset vs2026-x64-release --parallel 2
ctest --preset vs2026-x64-release --output-on-failure
```

長時間テストを明示的に有効化する場合:

```powershell
cmake --preset vs2026-x64-release -DHIPHOPMAX_ENABLE_LONG_RUN_TESTS=ON
cmake --build --preset vs2026-x64-release --parallel 2
ctest --test-dir build\vs2026-x64-release -C Release -R MTR014_meter_long_run --output-on-failure
```

`MTR014_meter_long_run` は実時間24時間の録音を待つテストではなく、48 kHz stereoの反復信号を24時間相当処理してHeap/Allocation/RSSをチェックするHarnessである。ただしCPU、Windows Heap観測、環境負荷の影響があるため、通常の軽量CIゲートへ常時含めない。

## 過去Release Recordとの境界

`V1_0_RELEASE_RECORD.md` の4/4 CTest PASS、MTR-014の24時間相当結果、Steinberg Validator、Cakewalk手動結果は過去の記録である。今回のブランチでコマンドを再実行していない限り、現在のPASSには再利用しない。Release Recordそのものは保全し、今回の確認結果を [`TEST_RESULT_2026-10-21.md`](TEST_RESULT_2026-10-21.md) に分離する。
