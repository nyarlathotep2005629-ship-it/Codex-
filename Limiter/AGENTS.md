# HipHopMax Development Instructions

- Documentation baseline: v0.3
- Scope of this package: design and test specification only; do not implement product code during the design-review task.

## Project goal

HipHopMax is a simple Windows x64 VST3 maximizer intended primarily for Hip-Hop mastering in Cakewalk Sonar.

The product goal is deliberately narrow:

- provide a conventional lookahead maximizer,
- expose only Drive, Ceiling, Release, Bypass, and meter-session reset to the user,
- show LUFS-I / LUFS-S / LUFS-M, True Peak, and Gain Reduction,
- show a Hip-Hop loudness guide (-9 to -7 LUFS) and selectable streaming references,
- remain stable and predictable in real-time DAW use.

Do not turn this project into a full mastering suite.

## Source of truth

Before implementing or modifying behavior, read these files in order:

1. `docs/01_requirements.md`
2. `docs/02_basic_design.md`
3. `docs/03_detailed_design.md`
4. `docs/04_test_plan.md`

If code and documentation disagree, stop and report the inconsistency. Do not silently redefine the specification.

## Architecture rules

- Keep the core maximizer DSP independent from JUCE where practical.
- Do not place all DSP logic in `PluginProcessor.cpp`.
- Separate host integration, DSP, metering, state, and GUI responsibilities.
- The audio output must never depend on the metering worker.
- The meter may become invalid if it cannot keep up; the audio thread must continue.
- Gain Reduction is published directly from the DSP to the GUI through an atomic value; it does not pass through the meter worker.
- The v1.0 audio bus contract is Stereo -> Stereo only.
- Publish `MeterSnapshot` through multiple atomic fields guarded by a sequence counter; readers must reject odd or changed sequences.
- Return the APVTS `bypass` parameter from `getBypassParameter()` so JUCE/VST3 exposes the same parameter as `kIsBypass`. `processBlockBypassed()` must preserve the fixed 5 ms latency.
- MeterWorker lifetime belongs to `PluginProcessor`, not the editor. Stop and join it before meter reconfiguration, `releaseResources()`, and processor destruction; editor open/close must not stop it.

## Real-time audio-thread rules

Code running from the audio callback must not:

- allocate or free heap memory,
- perform file I/O,
- perform network I/O,
- wait for a mutex or condition variable,
- access GUI objects,
- rebuild ValueTree state,
- initialize or destroy libebur128,
- emit high-volume logging.

Use preallocated buffers, atomics, and lock-free/SPSC communication where specified.

## Fixed v1 technology choices

- Language: C++20
- Plugin framework: JUCE 9.0.0
- Plugin format: VST3 x64
- Metering: libebur128 1.2.6
- Build system: CMake 4.2 or newer
- Compiler/toolchain: MSVC / Visual Studio 2026
- Target OS: Windows 11 x64
- Primary host: Cakewalk Sonar

Pin dependency tags/versions. Do not automatically move to a newer dependency without explicit approval.

Build libebur128 1.2.6 as a static library with `BUILD_SHARED_LIBS=OFF`. Its legacy CMake policy declaration must be handled by the documented policy-minimum PoC; do not silently patch or upgrade the dependency.

The libebur128 PoC mode candidate is exactly:

`EBUR128_MODE_S | EBUR128_MODE_I | EBUR128_MODE_TRUE_PEAK | EBUR128_MODE_HISTOGRAM`

Do not remove Histogram mode without passing the EBU representative tests and the long-run bounded-memory PoC.

## v1 scope discipline

Do not add these features unless explicitly requested:

- EQ,
- multiband processing,
- clipping/saturation stages,
- stereo imaging,
- AI mastering,
- automatic target matching,
- reference-track analysis,
- cloud/account features,
- AU/AAX formats,
- macOS support.
- mono bus support.

For v1.0, host automation is block-rate: read each parameter target once at the start of a process block. Do not claim sample-accurate automation.

The 5 ms linear lookahead attack ramp remains a PoC candidate, not a frozen v1.0 algorithm. Sample Ceiling safety is the final 100% Wet guarantee.

Use the canonical names `meterResetGeneration`, `meterAppliedGeneration`, and `schemaVersion`. Do not use legacy Boolean reset flags or alternate state-version names.

Keep plugin identity fixed as: name `HipHopMax`, vendor `HipHopMax Audio`, VST3 category `Fx|Mastering`, manufacturer code `Hhpa`, plugin code `Hmax`.

## Testing rule

Implement and run the tests defined in `docs/04_test_plan.md` for every relevant change.

A task is not complete if relevant automated tests fail, the VST3 validator fails, or the behavior differs from the design documents.

## Change management

If a requested change affects the DSP algorithm, latency, parameter meaning, metering semantics, or host I/O contract:

1. identify the affected requirement/design IDs,
2. update the documentation first or in the same change,
3. update tests,
4. then update implementation.
