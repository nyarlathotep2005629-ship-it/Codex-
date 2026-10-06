#pragma once

#include "MeterTypes.h"

#include <ebur128.h>

#include <cstddef>
#include <cstdint>

namespace hiphopmax::metering
{
class EbuR128Meter
{
public:
    EbuR128Meter() = default;
    ~EbuR128Meter();
    EbuR128Meter(const EbuR128Meter&) = delete;
    EbuR128Meter& operator=(const EbuR128Meter&) = delete;

    [[nodiscard]] bool configure(unsigned long sampleRate, unsigned int channels, bool histogram = true) noexcept;
    [[nodiscard]] bool reset() noexcept;
    [[nodiscard]] bool addInterleaved(const float* samples, std::size_t frames) noexcept;
    [[nodiscard]] MeterSnapshot measure(std::uint64_t generation) noexcept;
    [[nodiscard]] bool valid() const noexcept { return state_ != nullptr; }
    [[nodiscard]] std::uint64_t totalFrames() const noexcept { return totalFrames_; }

    static void mapLoudnessResult(int errorCode, double result, bool hasEnoughSamples,
                                  float& output, MeterFieldStatus& status) noexcept;

private:
    ebur128_state* state_ {};
    unsigned long sampleRate_ { 48000 };
    unsigned int channels_ { 2 };
    bool histogram_ { true };
    std::uint64_t totalFrames_ {};
    double sessionTruePeakLinear_ {};
};
}
