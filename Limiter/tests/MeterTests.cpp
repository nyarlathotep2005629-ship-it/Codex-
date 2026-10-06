#include "WavReader.h"
#include "metering/EbuR128Meter.h"
#include "metering/MeterFIFO.h"
#include "metering/MeterSnapshotPublisher.h"
#include "metering/MeterWorker.h"

#include <ebur128.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <iostream>
#include <limits>
#include <numbers>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace
{
using namespace hiphopmax::metering;

void require(bool condition, const std::string& message)
{
    if (!condition)
        throw std::runtime_error(message);
}

std::string asset(const char* name)
{
    return std::string(EBU_POC_DIR) + "/" + name;
}

MeterSnapshot measureFile(const char* name, bool histogram = true)
{
    const auto wav = readWav(asset(name));
    require(wav.channels == 2, std::string(name) + " is not stereo");
    EbuR128Meter meter;
    require(meter.configure(wav.sampleRate, wav.channels, histogram), "meter configure failed");
    constexpr std::size_t chunk = 4096;
    for (std::size_t offset = 0; offset < wav.frames(); offset += chunk)
    {
        const auto frames = std::min(chunk, wav.frames() - offset);
        require(meter.addInterleaved(wav.interleaved.data() + offset * wav.channels, frames), "add frames failed");
    }
    return meter.measure(1);
}

void requireLoudness(float actual, MeterFieldStatus status, float expected, const char* label)
{
    require(status == MeterFieldStatus::valid, std::string(label) + " not valid");
    require(std::abs(actual - expected) <= 0.1f, std::string(label) + " outside tolerance: " + std::to_string(actual));
}

void testEbuAccuracy()
{
    const auto one = measureFile("seq-3341-1-16bit.wav");
    requireLoudness(one.lufsMomentary, one.lufsMomentaryStatus, -23.0f, "MTR-001 3341-1 M");
    requireLoudness(one.lufsShortTerm, one.lufsShortTermStatus, -23.0f, "MTR-002 3341-1 S");
    requireLoudness(one.lufsIntegrated, one.lufsIntegratedStatus, -23.0f, "MTR-003 3341-1 I");
    const auto two = measureFile("seq-3341-2-16bit.wav");
    requireLoudness(two.lufsMomentary, two.lufsMomentaryStatus, -33.0f, "MTR-001 3341-2 M");
    requireLoudness(two.lufsShortTerm, two.lufsShortTermStatus, -33.0f, "MTR-002 3341-2 S");
    requireLoudness(two.lufsIntegrated, two.lufsIntegratedStatus, -33.0f, "MTR-003 3341-2 I");
    const auto three = measureFile("seq-3341-3-16bit-v02.wav");
    requireLoudness(three.lufsIntegrated, three.lufsIntegratedStatus, -23.0f, "MTR-003 3341-3 I");
    const auto five = measureFile("seq-3341-5-16bit-v02.wav");
    requireLoudness(five.lufsIntegrated, five.lufsIntegratedStatus, -23.0f, "MTR-003 3341-5 I");
    const auto nine = measureFile("seq-3341-9-24bit.wav");
    requireLoudness(nine.lufsShortTerm, nine.lufsShortTermStatus, -23.0f, "MTR-002 3341-9 S");
    const auto twelve = measureFile("seq-3341-12-24bit.wav");
    requireLoudness(twelve.lufsMomentary, twelve.lufsMomentaryStatus, -23.0f, "MTR-001 3341-12 M");
}

void testTruePeak()
{
    struct Case { const char* file; float expected; };
    for (const auto& item : { Case { "seq-3341-15-24bit.wav.wav", -6.0f },
                              Case { "seq-3341-19-24bit.wav.wav", 3.0f },
                              Case { "seq-3341-20-24bit.wav.wav", 0.0f } })
    {
        const auto result = measureFile(item.file);
        require(result.truePeakStatus == MeterFieldStatus::valid, "MTR-004 TP invalid");
        require(result.truePeakDbTP >= item.expected - 0.4f && result.truePeakDbTP <= item.expected + 0.2f,
                std::string("MTR-004 TP outside tolerance: ") + item.file + " " + std::to_string(result.truePeakDbTP));
    }
}

std::vector<float> sineInterleaved(unsigned long rate, double seconds, float amplitude)
{
    const auto frames = static_cast<std::size_t>(std::round(rate * seconds));
    std::vector<float> data(frames * 2);
    for (std::size_t i = 0; i < frames; ++i)
    {
        const auto x = amplitude * std::sin(static_cast<float>(2.0 * std::numbers::pi * 1000.0 * i / rate));
        data[i * 2] = data[i * 2 + 1] = x;
    }
    return data;
}

void testResetAndValidity()
{
    EbuR128Meter meter;
    require(meter.configure(48000, 2, true), "MTR-005 configure");
    auto loud = sineInterleaved(48000, 4.0, 0.5f);
    require(meter.addInterleaved(loud.data(), loud.size() / 2), "MTR-005 add A");
    const auto before = meter.measure(1);
    require(before.lufsIntegratedStatus == MeterFieldStatus::valid && before.truePeakStatus == MeterFieldStatus::valid, "MTR-005 initial validity");
    require(meter.reset(), "MTR-005 reset");
    const auto reset = meter.measure(2);
    require(reset.lufsIntegratedStatus == MeterFieldStatus::unmeasured && reset.lufsShortTermStatus == MeterFieldStatus::unmeasured
            && reset.lufsMomentaryStatus == MeterFieldStatus::unmeasured && reset.truePeakStatus == MeterFieldStatus::unmeasured,
            "MTR-005 reset did not invalidate all fields");
    auto quiet = sineInterleaved(48000, 0.5, 0.1f);
    require(meter.addInterleaved(quiet.data(), quiet.size() / 2), "MTR-005 add B");
    const auto partial = meter.measure(2);
    require(partial.lufsMomentaryStatus == MeterFieldStatus::valid && partial.lufsIntegratedStatus == MeterFieldStatus::valid
            && partial.lufsShortTermStatus == MeterFieldStatus::unmeasured && partial.truePeakStatus == MeterFieldStatus::valid,
            "MTR-010 per-field validity failed");
    require(partial.truePeakDbTP < before.truePeakDbTP, "MTR-009 old true peak leaked after reset");
}

bool waitUntil(const auto& predicate, std::chrono::milliseconds timeout = std::chrono::milliseconds(3000))
{
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline)
    {
        if (predicate()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(1));
    }
    return false;
}

void testFifoOverrunRecoveryAndGeneration()
{
    MeterFIFO fifo(8);
    MeterSnapshotPublisher publisher;
    std::atomic<std::uint64_t> resetGeneration { 0 };
    std::atomic<std::uint64_t> appliedGeneration { 0 };
    std::atomic<bool> overrun { false };
    MeterWorker worker(fifo, publisher, resetGeneration, appliedGeneration, overrun);
    require(worker.configure(48000, 2), "MTR worker configure");

    const auto start = std::chrono::steady_clock::now();
    int rejected = 0;
    for (int i = 0; i < 1000; ++i)
        if (!fifo.push({ 0.1f, 0.1f, 0 })) ++rejected;
    const auto elapsed = std::chrono::steady_clock::now() - start;
    require(rejected > 0 && elapsed < std::chrono::milliseconds(50), "MTR-007 FIFO did not fail non-blockingly");
    overrun.store(true);

    worker.requestReset(1);
    worker.requestReset(2);
    MeterSnapshot immediate;
    require(publisher.read(immediate) && immediate.generation == 2
            && immediate.lufsIntegratedStatus == MeterFieldStatus::unmeasured,
            "MTR-011 stopped reset was not immediate");
    fifo.clear();
    require(fifo.push({ 0.8f, 0.8f, 0 }), "MTR-011 old frame push");
    worker.start();
    require(waitUntil([&] { return appliedGeneration.load() == 2; }), "MTR-011 generation not applied");
    for (int i = 0; i < 30000; ++i)
    {
        while (!fifo.push({ 0.1f, 0.1f, 2 }))
            std::this_thread::yield();
    }
    require(waitUntil([&] { return worker.processedFrames() >= 30000; }), "MTR-008 worker did not recover");
    worker.stop();
    MeterSnapshot recovered;
    require(publisher.read(recovered) && recovered.generation == 2 && !recovered.meterOverrun,
            "MTR-008 recovery snapshot invalid");
    require(appliedGeneration.load() == 2, "MTR-011 reset lost");
}

void testHistogramReference()
{
    for (const auto* file : { "seq-3341-1-16bit.wav", "seq-3341-2-16bit.wav",
                              "seq-3341-3-16bit-v02.wav", "seq-3341-5-16bit-v02.wav" })
    {
        const auto histogram = measureFile(file, true);
        const auto reference = measureFile(file, false);
        require(histogram.lufsIntegratedStatus == MeterFieldStatus::valid && reference.lufsIntegratedStatus == MeterFieldStatus::valid,
                "MTR-013 invalid reference");
        require(std::abs(histogram.lufsIntegrated - reference.lufsIntegrated) <= 0.1f,
                std::string("MTR-013 histogram bias: ") + file);
        std::cout << "MTR-013 " << file << " histogram=" << histogram.lufsIntegrated
                  << " reference=" << reference.lufsIntegrated << '\n';
    }
}

void testSnapshotConcurrency()
{
    MeterSnapshotPublisher publisher;
    constexpr std::uint64_t writesPerThread = 5000000;
    std::atomic<bool> begin { false };
    std::atomic<bool> done { false };
    std::atomic<std::uint64_t> torn { 0 };
    auto writer = [&](std::uint64_t base)
    {
        while (!begin.load(std::memory_order_acquire)) {}
        for (std::uint64_t i = 1; i <= writesPerThread; ++i)
        {
            MeterSnapshot s;
            s.generation = base + i;
            const float marker = static_cast<float>(s.generation % 1000);
            s.lufsIntegrated = s.lufsShortTerm = s.lufsMomentary = s.truePeakDbTP = marker;
            s.lufsIntegratedStatus = s.lufsShortTermStatus = s.lufsMomentaryStatus = s.truePeakStatus = MeterFieldStatus::valid;
            publisher.publish(s);
            if ((i & 0x3fffU) == 0U) std::this_thread::yield();
        }
    };
    auto reader = [&]
    {
        MeterSnapshot last;
        while (!done.load(std::memory_order_acquire))
        {
            MeterSnapshot s = last;
            if (publisher.read(s, 2))
            {
                const float marker = static_cast<float>(s.generation % 1000);
                if (s.lufsIntegrated != marker || s.lufsShortTerm != marker || s.lufsMomentary != marker || s.truePeakDbTP != marker)
                    torn.fetch_add(1, std::memory_order_relaxed);
                last = s;
            }
        }
    };
    std::thread w1(writer, 0);
    std::thread w2(writer, 100000000);
    std::thread r1(reader);
    std::thread r2(reader);
    begin.store(true, std::memory_order_release);
    w1.join();
    w2.join();
    done.store(true, std::memory_order_release);
    r1.join();
    r2.join();
    require(torn.load() == 0, "MTR-015 torn snapshot observed");
    require((publisher.sequence() & 1U) == 0U, "MTR-015 sequence left odd");
}

void testWorkerLifecycle()
{
    MeterFIFO fifo(4096);
    MeterSnapshotPublisher publisher;
    std::atomic<std::uint64_t> resetGeneration { 0 };
    std::atomic<std::uint64_t> appliedGeneration { 0 };
    std::atomic<bool> overrun { false };
    MeterWorker worker(fifo, publisher, resetGeneration, appliedGeneration, overrun);
    for (int iteration = 0; iteration < 100; ++iteration)
    {
        const auto rate = iteration % 2 == 0 ? 44100UL : 96000UL;
        require(worker.configure(rate, 2), "MTR-016 configure failed");
        worker.requestReset(static_cast<std::uint64_t>(iteration + 1));
        worker.start();
        for (int i = 0; i < 100; ++i)
            require(fifo.push({ 0.0f, 0.0f, static_cast<std::uint64_t>(iteration + 1) }), "MTR-016 FIFO push failed");
        require(waitUntil([&] { return appliedGeneration.load() == static_cast<std::uint64_t>(iteration + 1); }), "MTR-016 generation not applied");
        worker.stop();
        require(!worker.isRunning(), "MTR-016 worker survived stop");
        fifo.clear();
    }
}

void testSilenceAndErrors()
{
    EbuR128Meter meter;
    require(meter.configure(48000, 2, true), "MTR-017 configure");
    std::vector<float> silence(48000 * 2, 0.0f);
    require(meter.addInterleaved(silence.data(), 48000), "MTR-017 silence add");
    const auto snapshot = meter.measure(1);
    require(snapshot.lufsIntegratedStatus == MeterFieldStatus::silence
            && snapshot.lufsMomentaryStatus == MeterFieldStatus::silence
            && snapshot.truePeakStatus == MeterFieldStatus::silence,
            "MTR-017 silence mapping failed");

    float output = 123.0f;
    MeterFieldStatus status = MeterFieldStatus::valid;
    EbuR128Meter::mapLoudnessResult(EBUR128_ERROR_INVALID_MODE, -23.0, true, output, status);
    require(status == MeterFieldStatus::error && std::isfinite(output), "MTR-017 API error mapping");
    for (const double bad : { std::numeric_limits<double>::quiet_NaN(), std::numeric_limits<double>::infinity() })
    {
        EbuR128Meter::mapLoudnessResult(EBUR128_SUCCESS, bad, true, output, status);
        require(status == MeterFieldStatus::error && std::isfinite(output), "MTR-017 non-finite mapping");
    }
    EbuR128Meter::mapLoudnessResult(EBUR128_SUCCESS, -std::numeric_limits<double>::infinity(), true, output, status);
    require(status == MeterFieldStatus::silence && std::isfinite(output), "MTR-017 -Inf mapping");
}

template <typename Function>
void run(const char* ids, Function&& function)
{
    function();
    std::cout << ids << " PASS\n";
}
}

int main()
{
    try
    {
        run("MTR-001/MTR-002/MTR-003", testEbuAccuracy);
        run("MTR-004", testTruePeak);
        run("MTR-005/MTR-009/MTR-010", testResetAndValidity);
        run("MTR-007/MTR-008/MTR-011/MTR-012", testFifoOverrunRecoveryAndGeneration);
        run("MTR-013", testHistogramReference);
        run("MTR-015", testSnapshotConcurrency);
        run("MTR-016", testWorkerLifecycle);
        run("MTR-017", testSilenceAndErrors);
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
    return 0;
}
