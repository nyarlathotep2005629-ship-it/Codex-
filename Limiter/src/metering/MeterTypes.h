#pragma once

#include <cstdint>

namespace hiphopmax::metering
{
enum class MeterFieldStatus : std::uint8_t
{
    unmeasured,
    valid,
    silence,
    error
};

struct MeterSnapshot
{
    float lufsIntegrated {};
    float lufsShortTerm {};
    float lufsMomentary {};
    float truePeakDbTP {};
    MeterFieldStatus lufsIntegratedStatus { MeterFieldStatus::unmeasured };
    MeterFieldStatus lufsShortTermStatus { MeterFieldStatus::unmeasured };
    MeterFieldStatus lufsMomentaryStatus { MeterFieldStatus::unmeasured };
    MeterFieldStatus truePeakStatus { MeterFieldStatus::unmeasured };
    bool meterOverrun {};
    std::uint64_t generation {};
};

struct MeterFrame
{
    float l {};
    float r {};
    std::uint64_t generation {};
};
}
