#pragma once

#include "MeterTypes.h"

#include <atomic>
#include <cstdint>
#include <mutex>

namespace hiphopmax::metering
{
class MeterSnapshotPublisher
{
public:
    void publish(MeterSnapshot snapshot);
    void publishUnmeasured(std::uint64_t generation);
    [[nodiscard]] bool read(MeterSnapshot& destination, int maxRetries = 8) const noexcept;
    [[nodiscard]] std::uint64_t sequence() const noexcept { return sequence_.load(std::memory_order_acquire); }

private:
    static void validateField(float& value, MeterFieldStatus& status) noexcept;

    mutable std::mutex writerMutex_;
    std::atomic<std::uint64_t> sequence_ { 0 };
    std::atomic<float> lufsIntegrated_ { 0.0f };
    std::atomic<float> lufsShortTerm_ { 0.0f };
    std::atomic<float> lufsMomentary_ { 0.0f };
    std::atomic<float> truePeakDbTP_ { 0.0f };
    std::atomic<std::uint8_t> lufsIntegratedStatus_ { static_cast<std::uint8_t>(MeterFieldStatus::unmeasured) };
    std::atomic<std::uint8_t> lufsShortTermStatus_ { static_cast<std::uint8_t>(MeterFieldStatus::unmeasured) };
    std::atomic<std::uint8_t> lufsMomentaryStatus_ { static_cast<std::uint8_t>(MeterFieldStatus::unmeasured) };
    std::atomic<std::uint8_t> truePeakStatus_ { static_cast<std::uint8_t>(MeterFieldStatus::unmeasured) };
    std::atomic<bool> meterOverrun_ { false };
    std::atomic<std::uint64_t> generation_ { 0 };
};
}
