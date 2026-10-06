#include "MeterSnapshotPublisher.h"

#include <cmath>

namespace hiphopmax::metering
{
void MeterSnapshotPublisher::publish(MeterSnapshot snapshot)
{
    validateField(snapshot.lufsIntegrated, snapshot.lufsIntegratedStatus);
    validateField(snapshot.lufsShortTerm, snapshot.lufsShortTermStatus);
    validateField(snapshot.lufsMomentary, snapshot.lufsMomentaryStatus);
    validateField(snapshot.truePeakDbTP, snapshot.truePeakStatus);

    const std::scoped_lock lock(writerMutex_);
    sequence_.fetch_add(1, std::memory_order_acq_rel);
    lufsIntegrated_.store(snapshot.lufsIntegrated, std::memory_order_relaxed);
    lufsShortTerm_.store(snapshot.lufsShortTerm, std::memory_order_relaxed);
    lufsMomentary_.store(snapshot.lufsMomentary, std::memory_order_relaxed);
    truePeakDbTP_.store(snapshot.truePeakDbTP, std::memory_order_relaxed);
    lufsIntegratedStatus_.store(static_cast<std::uint8_t>(snapshot.lufsIntegratedStatus), std::memory_order_relaxed);
    lufsShortTermStatus_.store(static_cast<std::uint8_t>(snapshot.lufsShortTermStatus), std::memory_order_relaxed);
    lufsMomentaryStatus_.store(static_cast<std::uint8_t>(snapshot.lufsMomentaryStatus), std::memory_order_relaxed);
    truePeakStatus_.store(static_cast<std::uint8_t>(snapshot.truePeakStatus), std::memory_order_relaxed);
    meterOverrun_.store(snapshot.meterOverrun, std::memory_order_relaxed);
    generation_.store(snapshot.generation, std::memory_order_relaxed);
    sequence_.fetch_add(1, std::memory_order_release);
}

void MeterSnapshotPublisher::publishUnmeasured(std::uint64_t generation)
{
    MeterSnapshot snapshot;
    snapshot.generation = generation;
    publish(snapshot);
}

bool MeterSnapshotPublisher::read(MeterSnapshot& destination, int maxRetries) const noexcept
{
    for (int attempt = 0; attempt < maxRetries; ++attempt)
    {
        const auto before = sequence_.load(std::memory_order_acquire);
        if ((before & 1U) != 0U)
            continue;

        MeterSnapshot candidate;
        candidate.lufsIntegrated = lufsIntegrated_.load(std::memory_order_relaxed);
        candidate.lufsShortTerm = lufsShortTerm_.load(std::memory_order_relaxed);
        candidate.lufsMomentary = lufsMomentary_.load(std::memory_order_relaxed);
        candidate.truePeakDbTP = truePeakDbTP_.load(std::memory_order_relaxed);
        candidate.lufsIntegratedStatus = static_cast<MeterFieldStatus>(lufsIntegratedStatus_.load(std::memory_order_relaxed));
        candidate.lufsShortTermStatus = static_cast<MeterFieldStatus>(lufsShortTermStatus_.load(std::memory_order_relaxed));
        candidate.lufsMomentaryStatus = static_cast<MeterFieldStatus>(lufsMomentaryStatus_.load(std::memory_order_relaxed));
        candidate.truePeakStatus = static_cast<MeterFieldStatus>(truePeakStatus_.load(std::memory_order_relaxed));
        candidate.meterOverrun = meterOverrun_.load(std::memory_order_relaxed);
        candidate.generation = generation_.load(std::memory_order_relaxed);

        const auto after = sequence_.load(std::memory_order_acquire);
        if (before == after && (after & 1U) == 0U)
        {
            destination = candidate;
            return true;
        }
    }
    return false;
}

void MeterSnapshotPublisher::validateField(float& value, MeterFieldStatus& status) noexcept
{
    if (status == MeterFieldStatus::valid && !std::isfinite(value))
    {
        value = 0.0f;
        status = MeterFieldStatus::error;
    }
    else if (!std::isfinite(value))
    {
        value = 0.0f;
    }
}
}
