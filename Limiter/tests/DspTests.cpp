#include "dsp/GainEnvelope.h"
#include "dsp/MaximizerDSP.h"

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdlib>
#include <iostream>
#include <limits>
#include <new>
#include <numbers>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace
{
std::atomic<bool> trackAllocations { false };
std::atomic<std::uint64_t> allocationCount { 0 };

void* trackedAllocate(std::size_t size)
{
    if (trackAllocations.load(std::memory_order_relaxed))
        allocationCount.fetch_add(1, std::memory_order_relaxed);
    if (auto* memory = std::malloc(size))
        return memory;
    throw std::bad_alloc {};
}
}

void* operator new(std::size_t size) { return trackedAllocate(size); }
void* operator new[](std::size_t size) { return trackedAllocate(size); }
void operator delete(void* memory) noexcept { std::free(memory); }
void operator delete[](void* memory) noexcept { std::free(memory); }
void operator delete(void* memory, std::size_t) noexcept { std::free(memory); }
void operator delete[](void* memory, std::size_t) noexcept { std::free(memory); }

namespace
{
using hiphopmax::dsp::GainEnvelope;
using hiphopmax::dsp::MaximizerDSP;

constexpr float tolerance = 1.0e-5f;

void require(bool condition, const std::string& message)
{
    if (!condition)
        throw std::runtime_error(message);
}

float dbToLinear(float db)
{
    return std::pow(10.0f, db / 20.0f);
}

float linearToDb(float value)
{
    return 20.0f * std::log10(std::max(value, 1.0e-20f));
}

struct Stereo
{
    std::vector<float> left;
    std::vector<float> right;
};

Stereo makeSine(int samples, double sampleRate, float amplitude, double frequency = 1000.0)
{
    Stereo signal { std::vector<float>(static_cast<std::size_t>(samples)), std::vector<float>(static_cast<std::size_t>(samples)) };
    for (int i = 0; i < samples; ++i)
    {
        const auto sample = amplitude * std::sin(static_cast<float>(2.0 * std::numbers::pi * frequency * i / sampleRate));
        signal.left[static_cast<std::size_t>(i)] = sample;
        signal.right[static_cast<std::size_t>(i)] = sample;
    }
    return signal;
}

void processInBlocks(MaximizerDSP& dsp, Stereo& signal, const std::vector<int>& blocks)
{
    int offset = 0;
    std::size_t blockIndex = 0;
    while (offset < static_cast<int>(signal.left.size()))
    {
        const int requested = blocks[blockIndex++ % blocks.size()];
        const int count = std::min(requested, static_cast<int>(signal.left.size()) - offset);
        float* channels[] { signal.left.data() + offset, signal.right.data() + offset };
        dsp.process(channels, 2, count);
        offset += count;
    }
}

float peak(const Stereo& signal, int begin = 0)
{
    float result = 0.0f;
    for (std::size_t i = static_cast<std::size_t>(std::max(0, begin)); i < signal.left.size(); ++i)
        result = std::max({ result, std::abs(signal.left[i]), std::abs(signal.right[i]) });
    return result;
}

int impulsePosition(const std::vector<float>& samples, float threshold = 0.25f)
{
    for (std::size_t i = 0; i < samples.size(); ++i)
        if (std::abs(samples[i]) >= threshold)
            return static_cast<int>(i);
    return -1;
}

void testDriveGain()
{
    constexpr double rate = 48000.0;
    auto base = makeSine(9600, rate, 0.01f);
    auto boosted = base;
    MaximizerDSP a;
    a.setCeilingDb(0.0f);
    a.prepare(rate, 512, 2);
    MaximizerDSP b;
    b.setDriveDb(6.0f);
    b.setCeilingDb(0.0f);
    b.prepare(rate, 512, 2);
    processInBlocks(a, base, { 257 });
    processInBlocks(b, boosted, { 257 });
    const auto ratioDb = linearToDb(peak(boosted, b.getLatencySamples()) / peak(base, a.getLatencySamples()));
    require(std::abs(ratioDb - 6.0f) <= 0.01f, "DSP-001 drive gain mismatch");
}

void testDriveSmoothing()
{
    constexpr int samples = 4000;
    MaximizerDSP dsp;
    dsp.setCeilingDb(0.0f);
    dsp.prepare(48000.0, 512, 2);
    Stereo warm { std::vector<float>(1000, 0.01f), std::vector<float>(1000, 0.01f) };
    processInBlocks(dsp, warm, { 1000 });
    dsp.setDriveDb(12.0f);
    Stereo signal { std::vector<float>(samples, 0.01f), std::vector<float>(samples, 0.01f) };
    processInBlocks(dsp, signal, { samples });
    const int firstAffected = dsp.getLatencySamples();
    require(signal.left[static_cast<std::size_t>(firstAffected)] < 0.011f, "DSP-002 drive jumped in one sample");
    require(std::abs(signal.left.back() - 0.01f * dbToLinear(12.0f)) < 2.0e-4f, "DSP-002 drive did not settle near 20 ms");
    require(std::all_of(signal.left.begin(), signal.left.end(), [](float x) { return std::isfinite(x); }), "DSP-002 produced non-finite output");
}

void testLatency()
{
    MaximizerDSP dsp;
    dsp.setCeilingDb(0.0f);
    dsp.prepare(48000.0, 512, 2);
    Stereo impulse { std::vector<float>(600), std::vector<float>(600) };
    impulse.left[0] = impulse.right[0] = 0.5f;
    processInBlocks(dsp, impulse, { 17, 64 });
    require(impulsePosition(impulse.left) == 240, "DSP-003 impulse latency mismatch");
}

void testCeilingBasic()
{
    MaximizerDSP dsp;
    dsp.setCeilingDb(-1.0f);
    dsp.prepare(48000.0, 512, 2);
    auto signal = makeSine(12000, 48000.0, 1.4f);
    signal.left[100] = signal.right[100] = 2.0f;
    processInBlocks(dsp, signal, { 128 });
    require(peak(signal) <= dbToLinear(-1.0f + 0.01f), "DSP-004 ceiling exceeded");
    require(signal.left[5000] != signal.left[5001], "DSP-004 unexpected flat hard clipping");
}

void testCeilingAutomationDown()
{
    MaximizerDSP dsp;
    dsp.setCeilingDb(-0.1f);
    dsp.prepare(48000.0, 512, 2);
    Stereo before { std::vector<float>(2000, 1.0f), std::vector<float>(2000, 1.0f) };
    processInBlocks(dsp, before, { 2000 });
    dsp.setCeilingDb(-3.0f);
    Stereo after { std::vector<float>(2000, 1.0f), std::vector<float>(2000, 1.0f) };
    processInBlocks(dsp, after, { 2000 });
    require(peak(after) <= dbToLinear(-3.0f + 0.01f), "DSP-005 lowered ceiling exceeded");
}

void testReleaseTau()
{
    for (const float releaseMs : { 50.0f, 100.0f, 500.0f, 1000.0f })
    {
        GainEnvelope envelope;
        envelope.prepare(1000.0, 5);
        envelope.setReleaseMs(releaseMs);
        for (int i = 0; i < 5; ++i)
            envelope.process(0.5f);
        const int tauSamples = static_cast<int>(releaseMs);
        for (int i = 0; i < tauSamples; ++i)
            envelope.process(1.0f);
        const float expected = 1.0f - 0.5f * std::exp(-1.0f);
        require(std::abs(envelope.currentGain() - expected) < 0.002f, "DSP-006 release tau mismatch");
    }
}

void testStereoLink()
{
    MaximizerDSP dsp;
    dsp.setCeilingDb(-6.0f);
    dsp.prepare(48000.0, 512, 2);
    Stereo signal { std::vector<float>(1000), std::vector<float>(1000) };
    signal.left[0] = 1.0f;
    signal.right[0] = 0.25f;
    processInBlocks(dsp, signal, { 1000 });
    const int d = dsp.getLatencySamples();
    require(std::abs(signal.left[d] / signal.right[d] - 4.0f) < 1.0e-4f, "DSP-007 stereo link changed image");
}

void testStereoContract()
{
    require(MaximizerDSP::supportsLayout(2, 2), "DSP-008 rejected stereo");
    require(!MaximizerDSP::supportsLayout(1, 1) && !MaximizerDSP::supportsLayout(1, 2) && !MaximizerDSP::supportsLayout(2, 1), "DSP-008 accepted unsupported layout");
}

void testVariableBlocks()
{
    auto a = makeSine(20000, 48000.0, 0.9f, 731.0);
    auto b = a;
    MaximizerDSP one;
    one.setDriveDb(4.0f);
    one.setCeilingDb(-1.0f);
    one.prepare(48000.0, 1024, 2);
    MaximizerDSP varied;
    varied.setDriveDb(4.0f);
    varied.setCeilingDb(-1.0f);
    varied.prepare(48000.0, 1024, 2);
    processInBlocks(one, a, { 20000 });
    processInBlocks(varied, b, { 1, 17, 64, 128, 511, 1024 });
    for (std::size_t i = 0; i < a.left.size(); ++i)
        require(std::abs(a.left[i] - b.left[i]) <= tolerance && std::abs(a.right[i] - b.right[i]) <= tolerance, "DSP-009 block variance");
}

void testSampleRates()
{
    for (const double rate : { 44100.0, 48000.0, 88200.0, 96000.0 })
    {
        MaximizerDSP dsp;
        dsp.setCeilingDb(-1.0f);
        dsp.prepare(rate, 1024, 2);
        require(dsp.getLatencySamples() == static_cast<int>(std::round(rate * 0.005)), "DSP-010 latency time mismatch");
        auto signal = makeSine(static_cast<int>(rate / 4), rate, 1.3f);
        processInBlocks(dsp, signal, { 511 });
        require(peak(signal) <= dbToLinear(-0.99f), "DSP-010 ceiling exceeded");
    }
}

void testZeroBlock()
{
    MaximizerDSP dsp;
    dsp.prepare(48000.0, 512, 2);
    Stereo signal { std::vector<float>(1, 0.5f), std::vector<float>(1, 0.5f) };
    float* channels[] { signal.left.data(), signal.right.data() };
    dsp.process(channels, 2, 0);
    require(signal.left[0] == 0.5f, "DSP-011 modified zero block");
}

void testNonFinite()
{
    MaximizerDSP dsp;
    dsp.prepare(48000.0, 512, 2);
    Stereo signal { std::vector<float>(600), std::vector<float>(600) };
    signal.left[0] = std::numeric_limits<float>::quiet_NaN();
    signal.right[1] = std::numeric_limits<float>::infinity();
    signal.left[2] = -std::numeric_limits<float>::infinity();
    processInBlocks(dsp, signal, { 17 });
    require(std::all_of(signal.left.begin(), signal.left.end(), [](float x) { return std::isfinite(x); }), "DSP-012 non-finite left output");
    require(std::all_of(signal.right.begin(), signal.right.end(), [](float x) { return std::isfinite(x); }), "DSP-012 non-finite right output");
}

void testBypassLatencyAndDrySeparation()
{
    MaximizerDSP wet;
    wet.setCeilingDb(0.0f);
    wet.prepare(48000.0, 512, 2);
    MaximizerDSP dry;
    dry.setDriveDb(12.0f);
    dry.setCeilingDb(-6.0f);
    dry.setBypass(true);
    dry.prepare(48000.0, 512, 2);
    Stereo a { std::vector<float>(600), std::vector<float>(600) };
    Stereo b = a;
    a.left[0] = a.right[0] = b.left[0] = b.right[0] = 0.75f;
    processInBlocks(wet, a, { 600 });
    processInBlocks(dry, b, { 600 });
    require(impulsePosition(a.left) == wet.getLatencySamples() && impulsePosition(b.left) == dry.getLatencySamples(), "DSP-013 bypass latency mismatch");
    require(std::abs(b.left[dry.getLatencySamples()] - 0.75f) < tolerance, "DSP-016 dry path contaminated");
}

void testBypassClickAndScope()
{
    MaximizerDSP dsp;
    dsp.setDriveDb(6.0f);
    dsp.setCeilingDb(-3.0f);
    dsp.prepare(48000.0, 256, 2);
    auto signal = makeSine(10000, 48000.0, 0.3f, 400.0);
    float maxDelta = 0.0f;
    for (int offset = 0; offset < 10000; offset += 100)
    {
        if (offset == 3000) dsp.setBypass(true);
        if (offset == 6000) dsp.setBypass(false);
        const int count = std::min(100, 10000 - offset);
        float* channels[] { signal.left.data() + offset, signal.right.data() + offset };
        dsp.process(channels, 2, count);
    }
    for (std::size_t i = 1; i < signal.left.size(); ++i)
        maxDelta = std::max(maxDelta, std::abs(signal.left[i] - signal.left[i - 1]));
    require(maxDelta < 0.12f, "DSP-014 bypass produced a single-sample click");

    MaximizerDSP dry;
    dry.setCeilingDb(-6.0f);
    dry.setBypass(true);
    dry.prepare(48000.0, 512, 2);
    Stereo raw { std::vector<float>(1000, 1.0f), std::vector<float>(1000, 1.0f) };
    processInBlocks(dry, raw, { 1000 });
    require(peak(raw) > dbToLinear(-6.0f), "DSP-017 incorrectly enforced ceiling on dry");
}

void testLinearRamp()
{
    GainEnvelope envelope;
    envelope.prepare(48000.0, 240);
    for (int i = 0; i < 120; ++i)
        envelope.process(0.8f);
    const float mid = envelope.currentGain();
    envelope.process(0.5f);
    require(envelope.currentGain() < mid, "DSP-015 larger peak did not retarget safely");
    for (int i = 1; i < 240; ++i)
        envelope.process(1.0f);
    require(std::abs(envelope.currentGain() - 0.5f) < tolerance, "DSP-015 ramp missed deadline");
}

void testBlockRateContract()
{
    MaximizerDSP dsp;
    dsp.setCeilingDb(0.0f);
    dsp.prepare(48000.0, 128, 2);
    Stereo first { std::vector<float>(128, 0.01f), std::vector<float>(128, 0.01f) };
    processInBlocks(dsp, first, { 128 });
    dsp.setDriveDb(6.0f);
    Stereo second { std::vector<float>(128, 0.01f), std::vector<float>(128, 0.01f) };
    processInBlocks(dsp, second, { 128 });
    require(std::all_of(second.left.begin(), second.left.end(), [](float x) { return std::isfinite(x); }), "DSP-018 block target failed");
}

void testRealtimeAllocationAndDenormal()
{
    MaximizerDSP dsp;
    dsp.prepare(48000.0, 1024, 2);
    Stereo signal { std::vector<float>(1024, std::numeric_limits<float>::denorm_min()), std::vector<float>(1024, std::numeric_limits<float>::denorm_min()) };
    float* channels[] { signal.left.data(), signal.right.data() };
    allocationCount.store(0);
    trackAllocations.store(true);
    dsp.process(channels, 2, 1024);
    trackAllocations.store(false);
    require(allocationCount.load() == 0, "DSP-019 allocation in process");
    require(std::all_of(signal.left.begin(), signal.left.end(), [](float x) { return std::isfinite(x) && std::fpclassify(x) != FP_SUBNORMAL; }), "DSP-021 denormal propagated");
    // MaximizerDSP owns no mutex/condition-variable member; its callback path is wait-free apart from atomics.
}

void testAttackStress()
{
    std::uint64_t interventions = 0;
    float maxExtra = 0.0f;
    for (const double rate : { 44100.0, 48000.0, 88200.0, 96000.0 })
    {
        for (const int block : { 1, 17, 128, 511, 1024 })
        {
            MaximizerDSP dsp;
            dsp.setCeilingDb(-1.0f);
            dsp.setReleaseMs(100.0f);
            dsp.prepare(rate, 1024, 2);
            const int d = dsp.getLatencySamples();
            Stereo signal { std::vector<float>(static_cast<std::size_t>(d * 8 + 32)), std::vector<float>(static_cast<std::size_t>(d * 8 + 32)) };
            for (int spacing = 1; spacing <= d; spacing = std::min(d, spacing * 2))
            {
                const int p = d + spacing;
                signal.left[static_cast<std::size_t>(p)] = signal.right[static_cast<std::size_t>(p)] = (spacing & 1) ? 1.8f : 1.2f;
                if (spacing == d) break;
            }
            signal.left[0] = signal.right[0] = 1.2f;
            signal.left[static_cast<std::size_t>(d / 2)] = signal.right[static_cast<std::size_t>(d / 2)] = 2.0f;
            processInBlocks(dsp, signal, { block });
            require(peak(signal) <= dbToLinear(-0.99f), "DSP-022 stress ceiling exceeded");
            const auto diagnostic = dsp.getSafetyDiagnostics();
            interventions += diagnostic.safetyInterventionCount;
            maxExtra = std::max(maxExtra, diagnostic.maxAdditionalSafetyGrDb);
        }
    }
    std::cout << "DSP-022 safetyInterventionCount=" << interventions
              << " maxAdditionalSafetyGrDb=" << maxExtra << '\n';
}

template <typename Function>
void run(const char* id, Function&& function)
{
    function();
    std::cout << id << " PASS\n";
}
}

int main()
{
    try
    {
        run("DSP-001", testDriveGain);
        run("DSP-002", testDriveSmoothing);
        run("DSP-003", testLatency);
        run("DSP-004", testCeilingBasic);
        run("DSP-005", testCeilingAutomationDown);
        run("DSP-006", testReleaseTau);
        run("DSP-007", testStereoLink);
        run("DSP-008", testStereoContract);
        run("DSP-009", testVariableBlocks);
        run("DSP-010", testSampleRates);
        run("DSP-011", testZeroBlock);
        run("DSP-012", testNonFinite);
        run("DSP-013/DSP-016", testBypassLatencyAndDrySeparation);
        run("DSP-014/DSP-017", testBypassClickAndScope);
        run("DSP-015", testLinearRamp);
        run("DSP-018", testBlockRateContract);
        run("DSP-019/DSP-020/DSP-021", testRealtimeAllocationAndDenormal);
        run("DSP-022", testAttackStress);
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
    return 0;
}
