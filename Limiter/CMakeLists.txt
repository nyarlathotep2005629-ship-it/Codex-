cmake_minimum_required(VERSION 4.2)

project(HipHopMax VERSION 1.0.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(JUCE_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.deps/JUCE-9.0.0")
set(LIBEBUR128_SOURCE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.deps/libebur128-1.2.6")

if(NOT EXISTS "${JUCE_SOURCE_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "JUCE 9.0.0 was not found at ${JUCE_SOURCE_DIR}")
endif()

if(NOT EXISTS "${LIBEBUR128_SOURCE_DIR}/CMakeLists.txt")
    message(FATAL_ERROR "libebur128 1.2.6 was not found at ${LIBEBUR128_SOURCE_DIR}")
endif()

add_subdirectory("${JUCE_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/_deps/juce")

# libebur128 1.2.6 declares cmake_minimum_required(VERSION 2.8.12).
# Keep HipHopMax on CMake 4.2 policies and raise only the dependency's policy
# compatibility floor while its subdirectory is configured (INT-006).
block(SCOPE_FOR VARIABLES)
    set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
    set(BUILD_SHARED_LIBS OFF CACHE BOOL "Build dependencies as static libraries" FORCE)
    set(ENABLE_TESTS OFF CACHE BOOL "Disable upstream libebur128 tests" FORCE)
    add_subdirectory("${LIBEBUR128_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/_deps/libebur128")
endblock()

juce_add_plugin(HipHopMax
    COMPANY_NAME "HipHopMax Audio"
    BUNDLE_ID audio.hiphopmax.HipHopMax
    PLUGIN_MANUFACTURER_CODE Hhpa
    PLUGIN_CODE Hmax
    FORMATS VST3
    PRODUCT_NAME "HipHopMax"
    PLUGIN_NAME "HipHopMax"
    DESCRIPTION "HipHopMax personal-use stereo maximizer"
    IS_SYNTH FALSE
    NEEDS_MIDI_INPUT FALSE
    NEEDS_MIDI_OUTPUT FALSE
    IS_MIDI_EFFECT FALSE
    EDITOR_WANTS_KEYBOARD_FOCUS FALSE
    COPY_PLUGIN_AFTER_BUILD FALSE
    VST3_CATEGORIES Fx Mastering)

target_sources(HipHopMax PRIVATE
    src/PluginProcessor.cpp
    src/PluginProcessor.h
    src/dsp/MaximizerDSP.cpp
    src/dsp/MaximizerDSP.h
    src/dsp/GainEnvelope.h
    src/dsp/LookaheadBuffer.h
    src/dsp/ParameterSmoother.h)

target_sources(HipHopMax PRIVATE
    src/metering/EbuR128Meter.cpp
    src/metering/EbuR128Meter.h
    src/metering/MeterFIFO.h
    src/metering/MeterSnapshotPublisher.cpp
    src/metering/MeterSnapshotPublisher.h
    src/metering/MeterTypes.h
    src/metering/MeterWorker.cpp
    src/metering/MeterWorker.h
    src/state/ParameterState.cpp
    src/state/ParameterState.h)

target_include_directories(HipHopMax PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${LIBEBUR128_SOURCE_DIR}/ebur128")

target_compile_definitions(HipHopMax PUBLIC
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0
    JUCE_VST3_CAN_REPLACE_VST2=0)

target_link_libraries(HipHopMax PRIVATE
    ebur128
    juce::juce_audio_utils
    juce::juce_recommended_config_flags
    juce::juce_recommended_lto_flags
    juce::juce_recommended_warning_flags)

add_executable(libebur128_smoke tests/LibEbur128Smoke.cpp)
target_include_directories(libebur128_smoke PRIVATE "${LIBEBUR128_SOURCE_DIR}/ebur128")
target_link_libraries(libebur128_smoke PRIVATE ebur128)

enable_testing()
add_test(NAME INT007_libebur128_static_smoke COMMAND libebur128_smoke)

add_executable(dsp_tests
    tests/DspTests.cpp
    src/dsp/MaximizerDSP.cpp)
target_include_directories(dsp_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
target_compile_features(dsp_tests PRIVATE cxx_std_20)
add_test(NAME DSP_PoC_Gate COMMAND dsp_tests)

add_executable(meter_tests
    tests/MeterTests.cpp
    src/metering/EbuR128Meter.cpp
    src/metering/MeterSnapshotPublisher.cpp
    src/metering/MeterWorker.cpp)
target_include_directories(meter_tests PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${CMAKE_CURRENT_SOURCE_DIR}/tests"
    "${LIBEBUR128_SOURCE_DIR}/ebur128")
target_compile_definitions(meter_tests PRIVATE
    EBU_POC_DIR="${CMAKE_CURRENT_SOURCE_DIR}/.downloads/ebu-v5.0-poc")
target_compile_features(meter_tests PRIVATE cxx_std_20)
target_link_libraries(meter_tests PRIVATE ebur128)
add_test(NAME Meter_PoC_Gate COMMAND meter_tests)

add_executable(meter_long_run
    tests/MeterLongRun.cpp
    src/metering/EbuR128Meter.cpp)
target_include_directories(meter_long_run PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${LIBEBUR128_SOURCE_DIR}/ebur128")
target_compile_features(meter_long_run PRIVATE cxx_std_20)
target_link_libraries(meter_long_run PRIVATE ebur128 Psapi)

add_executable(plugin_integration_tests
    tests/PluginIntegrationTests.cpp
    src/PluginProcessor.cpp
    src/dsp/MaximizerDSP.cpp
    src/metering/EbuR128Meter.cpp
    src/metering/MeterSnapshotPublisher.cpp
    src/metering/MeterWorker.cpp
    src/state/ParameterState.cpp)
target_include_directories(plugin_integration_tests PRIVATE
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
    "${LIBEBUR128_SOURCE_DIR}/ebur128")
target_compile_definitions(plugin_integration_tests PRIVATE
    JUCE_WEB_BROWSER=0
    JUCE_USE_CURL=0)
target_compile_features(plugin_integration_tests PRIVATE cxx_std_20)
target_link_libraries(plugin_integration_tests PRIVATE
    ebur128
    juce::juce_audio_utils
    juce::juce_recommended_config_flags
    juce::juce_recommended_warning_flags)
add_test(NAME Plugin_Integration_PoC COMMAND plugin_integration_tests)

if(WIN32)
    add_executable(minidump_stack tools/minidump_stack.cpp)
    target_link_libraries(minidump_stack PRIVATE Dbghelp)
endif()
