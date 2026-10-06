#pragma once

#include "GainEnvelope.h"
#include "LookaheadBuffer.h"
#include "ParameterSmoother.h"

#include <atomic>
#include <cstdint>

namespace hiphopmax::dsp
{
struct SafetyDiagnostics
{
    std::uint64_t safetyInterventionCount {};
    float maxAdditionalSafetyGrDb {};
};

class MaximizerDSP
{
public:
    void prepare(double sampleRate, int maxBlockSize, int numChannels);
    void reset() noexcept;

    void setDriveDb(float value) noexcept;
    void setCeilingDb(float value) noexcept;
    void setReleaseMs(float value) noexcept;
    void setBypass(bool enabled) noexcept;

    void process(float* const* channels, int numChannels, int numSamples) noexcept;

    [[nodiscard]] int getLatencySamples() const noexcept { return lookaheadSamples_; }
    [[nodiscard]] float getGainReductionDb() const noexcept { return gainReductionDb_.load(std::memory_order_relaxed); }
    [[nodiscard]] float getEnvelopeGain() const noexcept { return envelope_.currentGain(); }
    [[nodiscard]] SafetyDiagnostics getSafetyDiagnostics() const noexcept { return diagnostics_; }
    void clearSafetyDiagnostics() noexcept { diagnostics_ = {}; }
    [[nodiscard]] bool isPrepared() const noexcept { return prepared_; }
    [[nodiscard]] static constexpr bool supportsLayout(int inputs, int outputs) noexcept
    {
        return inputs == 2 && outputs == 2;
    }

private:
    [[nodiscard]] static float dbToLinear(float db) noexcept;
    [[nodiscard]] static float sanitize(float value) noexcept;

    LookaheadBuffer delay_;
    GainEnvelope envelope_;
    ParameterSmoother driveSmoother_;
    ParameterSmoother bypassSmoother_;
    std::atomic<float> gainReductionDb_ { 0.0f };
    SafetyDiagnostics diagnostics_;
    double sampleRate_ { 48000.0 };
    float driveDb_ { 0.0f };
    float ceilingDb_ { -1.0f };
    float ceilingLinear_ { 0.8912509f };
    float releaseMs_ { 100.0f };
    bool bypass_ { false };
    bool prepared_ { false };
    int lookaheadSamples_ { 240 };
};
}
