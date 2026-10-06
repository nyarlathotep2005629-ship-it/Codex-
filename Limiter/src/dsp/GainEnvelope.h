#pragma once

#include <algorithm>
#include <cmath>

namespace hiphopmax::dsp
{
class GainEnvelope
{
public:
    void prepare(double sampleRate, int lookaheadSamples) noexcept
    {
        sampleRate_ = std::max(1.0, sampleRate);
        rampLength_ = std::max(1, lookaheadSamples);
        setReleaseMs(releaseMs_);
        reset();
    }

    void reset() noexcept
    {
        currentGain_ = 1.0f;
        rampTarget_ = 1.0f;
        rampStep_ = 0.0f;
        rampRemaining_ = 0;
    }

    void setReleaseMs(float releaseMs) noexcept
    {
        releaseMs_ = std::clamp(releaseMs, 10.0f, 1000.0f);
        const auto seconds = static_cast<double>(releaseMs_) / 1000.0;
        releaseCoeff_ = static_cast<float>(std::exp(-1.0 / (seconds * sampleRate_)));
    }

    [[nodiscard]] float process(float requiredGain) noexcept
    {
        requiredGain = std::clamp(requiredGain, 0.0f, 1.0f);

        if (requiredGain < currentGain_ && (rampRemaining_ == 0 || requiredGain < rampTarget_))
        {
            rampTarget_ = requiredGain;
            rampRemaining_ = rampLength_;
            rampStep_ = (rampTarget_ - currentGain_) / static_cast<float>(rampRemaining_);
        }

        if (rampRemaining_ > 0)
        {
            currentGain_ += rampStep_;
            --rampRemaining_;
            if (rampRemaining_ == 0)
                currentGain_ = rampTarget_;
        }
        else
        {
            currentGain_ = releaseCoeff_ * currentGain_ + (1.0f - releaseCoeff_);
        }

        if (!std::isfinite(currentGain_))
            reset();

        currentGain_ = std::clamp(currentGain_, 0.0f, 1.0f);
        return currentGain_;
    }

    [[nodiscard]] float currentGain() const noexcept { return currentGain_; }
    [[nodiscard]] int rampRemaining() const noexcept { return rampRemaining_; }
    [[nodiscard]] float releaseCoefficient() const noexcept { return releaseCoeff_; }

private:
    double sampleRate_ { 48000.0 };
    float releaseMs_ { 100.0f };
    float releaseCoeff_ { 0.0f };
    float currentGain_ { 1.0f };
    float rampTarget_ { 1.0f };
    float rampStep_ { 0.0f };
    int rampLength_ { 240 };
    int rampRemaining_ { 0 };
};
}
