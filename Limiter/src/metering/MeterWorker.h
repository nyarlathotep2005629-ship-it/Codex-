#pragma once

#include "EbuR128Meter.h"
#include "MeterFIFO.h"
#include "MeterSnapshotPublisher.h"

#include <array>
#include <atomic>
#include <cstdint>
#include <thread>

namespace hiphopmax::metering
{
class MeterWorker
{
public:
    MeterWorker(MeterFIFO& fifo,
                MeterSnapshotPublisher& publisher,
                std::atomic<std::uint64_t>& meterResetGeneration,
                std::atomic<std::uint64_t>& meterAppliedGeneration,
                std::atomic<bool>& meterOverrun);
    ~MeterWorker();

    MeterWorker(const MeterWorker&) = delete;
    MeterWorker& operator=(const MeterWorker&) = delete;

    [[nodiscard]] bool configure(unsigned long sampleRate, unsigned int channels);
    void start();
    void stop() noexcept;
    void requestReset(std::uint64_t generation);
    [[nodiscard]] bool isRunning() const noexcept { return running_.load(std::memory_order_acquire); }
    [[nodiscard]] std::uint64_t processedFrames() const noexcept { return processedFrames_.load(std::memory_order_relaxed); }

private:
    void run() noexcept;
    void applyGeneration(std::uint64_t generation) noexcept;
    void publishOverrun(std::uint64_t generation);

    MeterFIFO& fifo_;
    MeterSnapshotPublisher& publisher_;
    std::atomic<std::uint64_t>& meterResetGeneration_;
    std::atomic<std::uint64_t>& meterAppliedGeneration_;
    std::atomic<bool>& meterOverrun_;
    EbuR128Meter meter_;
    std::thread thread_;
    std::atomic<bool> stopRequested_ { false };
    std::atomic<bool> running_ { false };
    std::atomic<std::uint64_t> processedFrames_ { 0 };
    unsigned long sampleRate_ { 48000 };
    unsigned int channels_ { 2 };
};
}
