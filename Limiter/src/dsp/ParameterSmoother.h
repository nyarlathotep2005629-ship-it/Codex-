#pragma once

#include <algorithm>
#include <cmath>

namespace hiphopmax::dsp
{
class ParameterSmoother
{
public:
    void prepare(double sampleRate, double rampSeconds) noexcept
    {
        rampSamples_ = std::max(1, static_cast<int>(std::round(sampleRate * rampSeconds)));
    }

    void reset(float value) noexcept
    {
        current_ = value;
        target_ = value;
        step_ = 0.0f;
        remaining_ = 0;
    }

    void setTarget(float value) noexcept
    {
        if (value == target_)
            return;
        target_ = value;
        remaining_ = rampSamples_;
        step_ = (target_ - current_) / static_cast<float>(remaining_);
    }

    [[nodiscard]] float next() noexcept
    {
        if (remaining_ > 0)
        {
            current_ += step_;
            if (--remaining_ == 0)
                current_ = target_;
        }
        return current_;
    }

    [[nodiscard]] float current() const noexcept { return current_; }
    [[nodiscard]] float target() const noexcept { return target_; }

private:
    float current_ { 1.0f };
    float target_ { 1.0f };
    float step_ { 0.0f };
    int rampSamples_ { 1 };
    int remaining_ { 0 };
};
}
