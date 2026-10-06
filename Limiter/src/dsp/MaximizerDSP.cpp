#include "MaximizerDSP.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace hiphopmax::dsp
{
void MaximizerDSP::prepare(double sampleRate, int, int numChannels)
{
    sampleRate_ = std::max(1.0, sampleRate);
    lookaheadSamples_ = std::max(1, static_cast<int>(std::round(sampleRate_ * 0.005)));
    delay_.prepare(lookaheadSamples_);
    envelope_.prepare(sampleRate_, lookaheadSamples_);
    envelope_.setReleaseMs(releaseMs_);
    driveSmoother_.prepare(sampleRate_, 0.020);
    driveSmoother_.reset(dbToLinear(driveDb_));
    bypassSmoother_.prepare(sampleRate_, 0.005);
    bypassSmoother_.reset(bypass_ ? 1.0f : 0.0f);
    ceilingLinear_ = dbToLinear(ceilingDb_);
    diagnostics_ = {};
    gainReductionDb_.store(0.0f, std::memory_order_relaxed);
    prepared_ = numChannels == 2;
}

void MaximizerDSP::reset() noexcept
{
    delay_.reset();
    envelope_.reset();
    driveSmoother_.reset(dbToLinear(driveDb_));
    bypassSmoother_.reset(bypass_ ? 1.0f : 0.0f);
    diagnostics_ = {};
    gainReductionDb_.store(0.0f, std::memory_order_relaxed);
}

void MaximizerDSP::setDriveDb(float value) noexcept
{
    driveDb_ = std::clamp(value, 0.0f, 18.0f);
    driveSmoother_.setTarget(dbToLinear(driveDb_));
}

void MaximizerDSP::setCeilingDb(float value) noexcept
{
    ceilingDb_ = std::clamp(value, -6.0f, 0.0f);
    ceilingLinear_ = dbToLinear(ceilingDb_);
}

void MaximizerDSP::setReleaseMs(float value) noexcept
{
    releaseMs_ = std::clamp(value, 10.0f, 1000.0f);
    envelope_.setReleaseMs(releaseMs_);
}

void MaximizerDSP::setBypass(bool enabled) noexcept
{
    bypass_ = enabled;
    bypassSmoother_.setTarget(enabled ? 1.0f : 0.0f);
}

void MaximizerDSP::process(float* const* channels, int numChannels, int numSamples) noexcept
{
    if (!prepared_ || numChannels != 2 || channels == nullptr || channels[0] == nullptr || channels[1] == nullptr || numSamples <= 0)
        return;

    auto* left = channels[0];
    auto* right = channels[1];
    float lastAppliedGain = 1.0f;

    for (int i = 0; i < numSamples; ++i)
    {
        const float rawL = sanitize(left[i]);
        const float rawR = sanitize(right[i]);
        const float drive = driveSmoother_.next();
        const float wetL = sanitize(rawL * drive);
        const float wetR = sanitize(rawR * drive);
        const float detectedPeak = std::max(std::abs(wetL), std::abs(wetR));
        const float requiredGain = detectedPeak > ceilingLinear_ && detectedPeak > 0.0f
            ? std::clamp(ceilingLinear_ / detectedPeak, 0.0f, 1.0f)
            : 1.0f;
        const float envelopeGain = envelope_.process(requiredGain);
        const auto delayed = delay_.push(rawL, rawR, wetL, wetR);
        const float delayedPeak = std::max(std::abs(delayed.wetL), std::abs(delayed.wetR));
        const float safetyGain = delayedPeak > ceilingLinear_ && delayedPeak > 0.0f
            ? std::clamp(ceilingLinear_ / delayedPeak, 0.0f, 1.0f)
            : 1.0f;
        const float appliedGain = std::min(envelopeGain, safetyGain);

        if (safetyGain + 1.0e-7f < envelopeGain)
        {
            ++diagnostics_.safetyInterventionCount;
            const float extra = -20.0f * std::log10(std::max(safetyGain / std::max(envelopeGain, 1.0e-12f), 1.0e-12f));
            diagnostics_.maxAdditionalSafetyGrDb = std::max(diagnostics_.maxAdditionalSafetyGrDb, extra);
        }

        const float processedL = delayed.wetL * appliedGain;
        const float processedR = delayed.wetR * appliedGain;
        const float dryMix = std::clamp(bypassSmoother_.next(), 0.0f, 1.0f);
        left[i] = sanitize(processedL * (1.0f - dryMix) + delayed.rawL * dryMix);
        right[i] = sanitize(processedR * (1.0f - dryMix) + delayed.rawR * dryMix);
        lastAppliedGain = appliedGain;
    }

    const float gr = 20.0f * std::log10(std::max(lastAppliedGain, 1.0e-12f));
    gainReductionDb_.store(std::isfinite(gr) ? std::min(0.0f, gr) : 0.0f, std::memory_order_relaxed);
}

float MaximizerDSP::dbToLinear(float db) noexcept
{
    const float result = std::pow(10.0f, db / 20.0f);
    return std::isfinite(result) ? result : 1.0f;
}

float MaximizerDSP::sanitize(float value) noexcept
{
    if (!std::isfinite(value) || std::fpclassify(value) == FP_SUBNORMAL)
        return 0.0f;
    return value;
}
}
