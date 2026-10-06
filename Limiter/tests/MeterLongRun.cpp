#include "metering/EbuR128Meter.h"

#include <Windows.h>
#include <Psapi.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <iostream>
#include <numbers>
#include <stdexcept>
#include <vector>

namespace
{
struct MemorySample
{
    std::uint64_t equivalentMinutes {};
    std::uint64_t busyBlocks {};
    std::uint64_t busyBytes {};
    std::uint64_t rssBytes {};
};

MemorySample sampleMemory(std::uint64_t equivalentMinutes)
{
    MemorySample result;
    result.equivalentMinutes = equivalentMinutes;

    const DWORD heapCount = GetProcessHeaps(0, nullptr);
    std::vector<HANDLE> heaps(heapCount);
    if (GetProcessHeaps(heapCount, heaps.data()) != heapCount)
        throw std::runtime_error("GetProcessHeaps failed");
    for (const auto heap : heaps)
    {
        if (!HeapLock(heap))
            continue;
        PROCESS_HEAP_ENTRY entry {};
        while (HeapWalk(heap, &entry))
        {
            if ((entry.wFlags & PROCESS_HEAP_ENTRY_BUSY) != 0)
            {
                ++result.busyBlocks;
                result.busyBytes += entry.cbData;
            }
        }
        HeapUnlock(heap);
    }

    PROCESS_MEMORY_COUNTERS_EX counters {};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(GetCurrentProcess(), reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters), sizeof(counters)))
        throw std::runtime_error("GetProcessMemoryInfo failed");
    result.rssBytes = counters.WorkingSetSize;
    return result;
}

void require(bool condition, const char* message)
{
    if (!condition)
        throw std::runtime_error(message);
}
}

int main()
{
    try
    {
        constexpr unsigned long sampleRate = 48000;
        constexpr std::uint64_t totalSeconds = 24ULL * 60ULL * 60ULL;
        constexpr std::uint64_t warmupSeconds = 10ULL * 60ULL;
        constexpr std::uint64_t intervalSeconds = 10ULL * 60ULL;
        constexpr std::size_t chunkFrames = sampleRate * 10ULL;

        std::vector<float> signal(chunkFrames * 2);
        for (std::size_t i = 0; i < chunkFrames; ++i)
        {
            const auto x = 0.1f * std::sin(static_cast<float>(2.0 * std::numbers::pi * 997.0 * i / sampleRate));
            signal[i * 2] = signal[i * 2 + 1] = x;
        }

        hiphopmax::metering::EbuR128Meter meter;
        require(meter.configure(sampleRate, 2, true), "MTR-014 configure failed");
        std::vector<MemorySample> samples;
        samples.reserve(static_cast<std::size_t>(totalSeconds / intervalSeconds + 1));

        for (std::uint64_t seconds = 0; seconds < totalSeconds; seconds += 10)
        {
            require(meter.addInterleaved(signal.data(), chunkFrames), "MTR-014 add frames failed");
            const auto elapsed = seconds + 10;
            if (elapsed >= warmupSeconds && elapsed % intervalSeconds == 0)
                samples.push_back(sampleMemory(elapsed / 60));
        }

        require(!samples.empty(), "MTR-014 no samples");
        const auto baseline = samples.front();
        const auto final = samples.back();
        std::uint64_t maxBytes = baseline.busyBytes;
        std::uint64_t maxBlocks = baseline.busyBlocks;
        bool strictlyMonotonic = true;
        for (std::size_t i = 1; i < samples.size(); ++i)
        {
            maxBytes = (std::max)(maxBytes, samples[i].busyBytes);
            maxBlocks = (std::max)(maxBlocks, samples[i].busyBlocks);
            if (samples[i].busyBytes <= samples[i - 1].busyBytes)
                strictlyMonotonic = false;
        }

        require(final.busyBytes <= baseline.busyBytes + 1024ULL * 1024ULL,
                "MTR-014 final live heap exceeds warm-up + 1 MiB");
        require(maxBlocks <= baseline.busyBlocks + 4,
                "MTR-014 live allocation count increased");
        require(!strictlyMonotonic, "MTR-014 heap grew monotonically");

        std::cout << "MTR-014 equivalentHours=24 checkpoints=" << samples.size()
                  << " baselineHeapBytes=" << baseline.busyBytes
                  << " finalHeapBytes=" << final.busyBytes
                  << " maxHeapBytes=" << maxBytes
                  << " baselineBlocks=" << baseline.busyBlocks
                  << " finalBlocks=" << final.busyBlocks
                  << " maxBlocks=" << maxBlocks
                  << " baselineRSS=" << baseline.rssBytes
                  << " finalRSS=" << final.rssBytes << '\n';
        std::cout << "MTR-014 PASS\n";
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
    return 0;
}
