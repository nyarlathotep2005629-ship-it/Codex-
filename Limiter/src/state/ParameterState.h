#pragma once

#include <juce_audio_processors/juce_audio_processors.h>

namespace hiphopmax::state
{
class ParameterState
{
public:
    static constexpr int currentSchemaVersion = 1;
    explicit ParameterState(juce::AudioProcessor& processor);
    [[nodiscard]] float driveDb() const noexcept;
    [[nodiscard]] float ceilingDb() const noexcept;
    [[nodiscard]] float releaseMs() const noexcept;
    [[nodiscard]] bool bypass() const noexcept;
    [[nodiscard]] juce::AudioProcessorParameter* bypassParameter() const noexcept;
    void save(juce::MemoryBlock& destination);
    void restore(const void* data, int sizeInBytes);
    void resetToDefaults();
    juce::AudioProcessorValueTreeState apvts;

private:
    static juce::AudioProcessorValueTreeState::ParameterLayout createLayout();
    void setParameter(const char* id, float plainValue);
    std::atomic<float>* drive_ {};
    std::atomic<float>* ceiling_ {};
    std::atomic<float>* release_ {};
    std::atomic<float>* bypass_ {};
};
}
