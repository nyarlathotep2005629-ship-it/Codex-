#pragma once

#include <algorithm>
#include <vector>

namespace hiphopmax::dsp
{
struct DelayedStereoFrame
{
    float rawL {};
    float rawR {};
    float wetL {};
    float wetR {};
};

class LookaheadBuffer
{
public:
    void prepare(int delaySamples)
    {
        delaySamples_ = std::max(1, delaySamples);
        rawL_.assign(static_cast<std::size_t>(delaySamples_), 0.0f);
        rawR_.assign(static_cast<std::size_t>(delaySamples_), 0.0f);
        wetL_.assign(static_cast<std::size_t>(delaySamples_), 0.0f);
        wetR_.assign(static_cast<std::size_t>(delaySamples_), 0.0f);
        writeIndex_ = 0;
    }

    void reset() noexcept
    {
        std::fill(rawL_.begin(), rawL_.end(), 0.0f);
        std::fill(rawR_.begin(), rawR_.end(), 0.0f);
        std::fill(wetL_.begin(), wetL_.end(), 0.0f);
        std::fill(wetR_.begin(), wetR_.end(), 0.0f);
        writeIndex_ = 0;
    }

    [[nodiscard]] DelayedStereoFrame push(float rawL, float rawR, float wetL, float wetR) noexcept
    {
        const auto index = static_cast<std::size_t>(writeIndex_);
        const DelayedStereoFrame delayed { rawL_[index], rawR_[index], wetL_[index], wetR_[index] };
        rawL_[index] = rawL;
        rawR_[index] = rawR;
        wetL_[index] = wetL;
        wetR_[index] = wetR;
        if (++writeIndex_ == delaySamples_)
            writeIndex_ = 0;
        return delayed;
    }

private:
    std::vector<float> rawL_;
    std::vector<float> rawR_;
    std::vector<float> wetL_;
    std::vector<float> wetR_;
    int delaySamples_ { 1 };
    int writeIndex_ { 0 };
};
}
