#include "EbuR128Meter.h"

#include <ebur128.h>

#include <algorithm>
#include <cmath>
#include <limits>

namespace hiphopmax::metering
{
EbuR128Meter::~EbuR128Meter()
{
    if (state_ != nullptr)
        ebur128_destroy(&state_);
}

bool EbuR128Meter::configure(unsigned long sampleRate, unsigned int channels, bool histogram) noexcept
{
    if (state_ != nullptr)
        ebur128_destroy(&state_);
    sampleRate_ = sampleRate;
    channels_ = channels;
    histogram_ = histogram;
    totalFrames_ = 0;
    sessionTruePeakLinear_ = 0.0;
    int mode = EBUR128_MODE_S | EBUR128_MODE_I | EBUR128_MODE_TRUE_PEAK;
    if (histogram_)
        mode |= EBUR128_MODE_HISTOGRAM;
    state_ = ebur128_init(channels_, sampleRate_, mode);
    return state_ != nullptr;
}

bool EbuR128Meter::reset() noexcept
{
    return configure(sampleRate_, channels_, histogram_);
}

bool EbuR128Meter::addInterleaved(const float* samples, std::size_t frames) noexcept
{
    if (state_ == nullptr || samples == nullptr)
        return false;
    if (ebur128_add_frames_float(state_, samples, frames) != EBUR128_SUCCESS)
        return false;
    totalFrames_ += frames;

    double left = 0.0;
    double right = 0.0;
    if (ebur128_true_peak(state_, 0, &left) == EBUR128_SUCCESS
        && ebur128_true_peak(state_, 1, &right) == EBUR128_SUCCESS
        && std::isfinite(left) && std::isfinite(right))
        sessionTruePeakLinear_ = std::max(sessionTruePeakLinear_, std::max(left, right));
    return true;
}

MeterSnapshot EbuR128Meter::measure(std::uint64_t generation) noexcept
{
    MeterSnapshot snapshot;
    snapshot.generation = generation;
    if (state_ == nullptr)
    {
        snapshot.lufsIntegratedStatus = MeterFieldStatus::error;
        snapshot.lufsShortTermStatus = MeterFieldStatus::error;
        snapshot.lufsMomentaryStatus = MeterFieldStatus::error;
        snapshot.truePeakStatus = MeterFieldStatus::error;
        return snapshot;
    }

    double value = 0.0;
    auto error = ebur128_loudness_momentary(state_, &value);
    mapLoudnessResult(error, value, totalFrames_ >= static_cast<std::uint64_t>(std::ceil(sampleRate_ * 0.4)),
                      snapshot.lufsMomentary, snapshot.lufsMomentaryStatus);
    value = 0.0;
    error = ebur128_loudness_shortterm(state_, &value);
    mapLoudnessResult(error, value, totalFrames_ >= static_cast<std::uint64_t>(sampleRate_) * 3U,
                      snapshot.lufsShortTerm, snapshot.lufsShortTermStatus);
    value = 0.0;
    error = ebur128_loudness_global(state_, &value);
    mapLoudnessResult(error, value, totalFrames_ >= static_cast<std::uint64_t>(std::ceil(sampleRate_ * 0.4)),
                      snapshot.lufsIntegrated, snapshot.lufsIntegratedStatus);

    if (totalFrames_ == 0)
    {
        snapshot.truePeakStatus = MeterFieldStatus::unmeasured;
    }
    else if (sessionTruePeakLinear_ == 0.0)
    {
        snapshot.truePeakStatus = MeterFieldStatus::silence;
    }
    else if (std::isfinite(sessionTruePeakLinear_) && sessionTruePeakLinear_ > 0.0)
    {
        const auto db = 20.0 * std::log10(sessionTruePeakLinear_);
        if (std::isfinite(db))
        {
            snapshot.truePeakDbTP = static_cast<float>(db);
            snapshot.truePeakStatus = MeterFieldStatus::valid;
        }
        else
        {
            snapshot.truePeakStatus = MeterFieldStatus::error;
        }
    }
    else
    {
        snapshot.truePeakStatus = MeterFieldStatus::error;
    }
    return snapshot;
}

void EbuR128Meter::mapLoudnessResult(int errorCode, double result, bool hasEnoughSamples,
                                    float& output, MeterFieldStatus& status) noexcept
{
    output = 0.0f;
    if (!hasEnoughSamples)
    {
        status = MeterFieldStatus::unmeasured;
        return;
    }
    if (errorCode != EBUR128_SUCCESS)
    {
        status = MeterFieldStatus::error;
        return;
    }
    if (std::isinf(result) && result < 0.0)
    {
        status = MeterFieldStatus::silence;
        return;
    }
    if (!std::isfinite(result))
    {
        status = MeterFieldStatus::error;
        return;
    }
    output = static_cast<float>(result);
    status = MeterFieldStatus::valid;
}
}
