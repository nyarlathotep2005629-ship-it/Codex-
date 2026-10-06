# HipHop Maximizer Build PoC Results

- Date: 2026-08-15
- Scope: Build PoC, Empty VST3 host recognition, EBU asset integrity
- Result: PASS
- Out of scope: Limiter DSP, production meter, bypass, GUI

## Toolchain and dependencies

- Windows x64
- CMake 4.2.1 (`cmake_minimum_required(VERSION 4.2)`)
- Visual Studio Build Tools 2026 / MSVC 19.51.36256.0
- Windows SDK 10.0.26100.0
- JUCE 9.0.0
- libebur128 1.2.6
- `BUILD_SHARED_LIBS=OFF`

Downloaded source/tool archive SHA-256:

- CMake 4.2.1 ZIP: `dfc2b2afac257555e3b9ce375b12b2883964283a366c17fec96cf4d17e4f1677`
- JUCE 9.0.0 ZIP: `bfd8dcd4af67223dedb4a26e9f3e9bea8960ed5528efdfbd891aded74ab05248`
- libebur128 1.2.6 ZIP: `edd60c1ed2ff45711ee1c372e723a1deda376ba9060c23fcc9262efddaee141b`

## Clean build matrix

| Configuration | Configure | Build | CTest |
|---|---:|---:|---:|
| Debug x64 | PASS | PASS | 1/1 PASS |
| Release x64 | PASS | PASS | 1/1 PASS |

The Japanese workspace path caused JUCE-generated helper execution to fail. Both clean builds were therefore run through an ASCII-only junction, `C:\Users\hiroki\Documents\HipHopMaxPoC`, which resolves to this workspace. No source path was changed.

## INT-006 CMake 4.2 Policy Compatibility

PASS.

- Top-level policy remains CMake 4.2.
- Only the libebur128 `add_subdirectory` block sets `CMAKE_POLICY_VERSION_MINIMUM=3.5`.
- `BUILD_SHARED_LIBS=OFF` and `ENABLE_TESTS=OFF` are scoped to that block.
- Independent Debug and Release clean build trees configured and built successfully.

## INT-007 libebur128 Static Link

PASS.

- Debug and Release produced `ebur128.lib`.
- `INT007_libebur128_static_smoke` passed in both configurations.
- The smoke test initializes libebur128, submits one second of stereo digital silence, reads integrated loudness, and verifies the documented/error-safe negative-infinity result.
- `dumpbin /DEPENDENTS` on the Release VST3 showed no libebur128 runtime DLL dependency.

## Empty VST3 and host recognition

PASS.

Release bundle:

`build\vs2026-x64-release\HipHopMax_artefacts\Release\VST3\HipHopMax.vst3`

Release module binary SHA-256:

`b2a8ba4b298738a4638d1551b89506234914685c146cb4309fef32112e2cc6b`

Generated metadata:

- Plugin/Product: `HipHopMax`
- Vendor/Company: `HipHopMax Audio`
- VST3 categories: `Fx|Mastering`
- Manufacturer code: `Hhpa`
- Plugin code: `Hmax`
- Bundle ID: `audio.hiphopmax.HipHopMax`

Cakewalk Sonar 2026.02 (Build 055, 64-bit) recognized the plugin. Its plug-in menu cache lists `HipHopMax` under `Mastering` with class ID `{ABCDEF01-9182-FAEB-4868-7061486D6178}`, matching the generated VST3 module metadata.

The standard `C:\Program Files\Common Files\VST3` copy was blocked by the managed-session ACL. No security setting was changed. Cakewalk's configured recursive Documents scan path recognized the Release bundle directly from the build output.

## EBU Loudness Test Set v5.0 integrity

Archive size: 91,631,421 bytes

Archive SHA-256:

`9cc500b4df83f7c21855c74dce795ef5209a752bf884253ae57d0ce512efb062`

All nine PoC files match the v0.3 test plan:

| File | SHA-256 | Result |
|---|---|---:|
| `seq-3341-1-16bit.wav` | `50dff94bf941756cae2b3e1a67931a28e1605a876be1b7f3707f2ffcf485955c` | PASS |
| `seq-3341-2-16bit.wav` | `7c9399188aed09980c76a67adc36b33f2d61c3e0c8457d83f7bf879122534ce3` | PASS |
| `seq-3341-3-16bit-v02.wav` | `1fced6ac2397d4337257185908bd49bb18314f441a0daf0ef602f671254d9efb` | PASS |
| `seq-3341-5-16bit-v02.wav` | `7acd129ddebb1935980585742075ad4aa82abe1147f7ce8abd28346b49561e61` | PASS |
| `seq-3341-9-24bit.wav` | `8157f3e794eac577b6007586fe47f51db8bfdc636729e6a773928c72791ef0f2` | PASS |
| `seq-3341-12-24bit.wav` | `d7fc88e31003553bb1329e84a8921217ae7c58045dfd620bc6b530d399a6933b` | PASS |
| `seq-3341-15-24bit.wav.wav` | `86f8a70eca1bc77623dd20a4acbadbc449711cd348615d852e3b0689827bea55` | PASS |
| `seq-3341-19-24bit.wav.wav` | `31bda52dc66bb63aa0d04b2e5a1e6a4332296fa5635570469b2863d05a06d309` | PASS |
| `seq-3341-20-24bit.wav.wav` | `a2df8791198781579a25e0437008ce9b1a48b13605a8b71886a360c4ceed16e4` | PASS |

## Gate decision

The requested Build PoC scope is complete. It is safe to proceed to the next PoC stage defined by the design, but the full PoC Gate is not yet complete. Limiter DSP implementation has not started.
