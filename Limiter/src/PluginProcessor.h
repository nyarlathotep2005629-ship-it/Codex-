#pragma once

#include "dsp/MaximizerDSP.h"
#include "metering/MeterFIFO.h"
#include "metering/MeterSnapshotPublisher.h"
#include "metering/MeterWorker.h"
#include "state/ParameterState.h"

#include <juce_audio_processors/juce_audio_processors.h>

#include <atomic>

class HipHopMaxAudioProcessor final : public juce::AudioProcessor
{
public:
    HipHopMaxAudioProcessor();
    ~HipHopMaxAudioProcessor() override;

    void prepareToPlay(double sampleRate, int maximumExpectedSamplesPerBlock) override;
    void releaseResources() override;
    bool isBusesLayoutSupported(const BusesLayout& layouts) const override;
    void processBlock(juce::AudioBuffer<float>& buffer, juce::MidiBuffer& midiMessages) override;
    void processBlockBypassed(juce::AudioBuffer<float>& buffer, juce::MidiBuffer& midiMessages) override;
    juce::AudioProcessorParameter* getBypassParameter() const override;
    bool supportsDoublePrecisionProcessing() const override;

    juce::AudioProcessorEditor* createEditor() override;
    bool hasEditor() const override;

    const juce::String getName() const override;
    bool acceptsMidi() const override;
    bool producesMidi() const override;
    bool isMidiEffect() const override;
    double getTailLengthSeconds() const override;

    int getNumPrograms() override;
    int getCurrentProgram() override;
    void setCurrentProgram(int index) override;
    const juce::String getProgramName(int index) override;
    void changeProgramName(int index, const juce::String& newName) override;

    void getStateInformation(juce::MemoryBlock& destinationData) override;
    void setStateInformation(const void* data, int sizeInBytes) override;

    void resetMeterSession();
    [[nodiscard]] bool readMeterSnapshot(hiphopmax::metering::MeterSnapshot& snapshot) const noexcept;
    [[nodiscard]] float getGainReductionDb() const noexcept;
    [[nodiscard]] hiphopmax::state::ParameterState& parameters() noexcept { return parameters_; }
    [[nodiscard]] bool isMeterWorkerRunning() const noexcept { return meterWorker_.isRunning(); }
    [[nodiscard]] std::uint64_t meterResetGeneration() const noexcept { return meterResetGeneration_.load(std::memory_order_acquire); }
    [[nodiscard]] std::uint64_t meterAppliedGeneration() const noexcept { return meterAppliedGeneration_.load(std::memory_order_acquire); }

private:
    void processCommon(juce::AudioBuffer<float>& buffer, bool forceHostBypass) noexcept;

    hiphopmax::state::ParameterState parameters_;
    hiphopmax::dsp::MaximizerDSP maximizer_;
    hiphopmax::metering::MeterFIFO meterFifo_;
    hiphopmax::metering::MeterSnapshotPublisher meterPublisher_;
    std::atomic<std::uint64_t> meterResetGeneration_ { 0 };
    std::atomic<std::uint64_t> meterAppliedGeneration_ { 0 };
    std::atomic<bool> meterOverrun_ { false };
    std::atomic<bool> meterFifoWriteEnabled_ { false };
    hiphopmax::metering::MeterWorker meterWorker_;

    JUCE_DECLARE_NON_COPYABLE_WITH_LEAK_DETECTOR(HipHopMaxAudioProcessor)
};
