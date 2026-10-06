#include "ParameterState.h"

namespace hiphopmax::state
{
ParameterState::ParameterState(juce::AudioProcessor& processor)
    : apvts(processor, nullptr, "HipHopMaxState", createLayout())
{
    drive_ = apvts.getRawParameterValue("drive");
    ceiling_ = apvts.getRawParameterValue("ceiling");
    release_ = apvts.getRawParameterValue("release");
    bypass_ = apvts.getRawParameterValue("bypass");
}

float ParameterState::driveDb() const noexcept { return drive_->load(std::memory_order_relaxed); }
float ParameterState::ceilingDb() const noexcept { return ceiling_->load(std::memory_order_relaxed); }
float ParameterState::releaseMs() const noexcept { return release_->load(std::memory_order_relaxed); }
bool ParameterState::bypass() const noexcept { return bypass_->load(std::memory_order_relaxed) >= 0.5f; }
juce::AudioProcessorParameter* ParameterState::bypassParameter() const noexcept { return apvts.getParameter("bypass"); }

void ParameterState::save(juce::MemoryBlock& destination)
{
    auto snapshot = apvts.copyState();
    snapshot.setProperty("schemaVersion", currentSchemaVersion, nullptr);
    if (const auto xml = snapshot.createXml())
    {
        const auto text = xml->toString();
        destination.replaceAll(text.toRawUTF8(), static_cast<std::size_t>(text.getNumBytesAsUTF8()));
    }
}

void ParameterState::restore(const void* data, int sizeInBytes)
{
    const auto text = juce::String::fromUTF8(static_cast<const char*>(data), sizeInBytes);
    const auto xml = juce::parseXML(text);
    if (xml == nullptr || !xml->hasTagName(apvts.state.getType().toString()))
    {
        resetToDefaults();
        return;
    }
    auto restored = juce::ValueTree::fromXml(*xml);
    const int schemaVersion = restored.hasProperty("schemaVersion")
        ? static_cast<int>(restored.getProperty("schemaVersion")) : 0;
    if (schemaVersion > currentSchemaVersion || schemaVersion < 0)
    {
        resetToDefaults();
        return;
    }
    restored.setProperty("schemaVersion", currentSchemaVersion, nullptr);
    apvts.replaceState(restored);
}

void ParameterState::resetToDefaults()
{
    setParameter("drive", 0.0f);
    setParameter("ceiling", -1.0f);
    setParameter("release", 100.0f);
    setParameter("bypass", 0.0f);
    setParameter("referencePlatform", 0.0f);
    apvts.state.setProperty("schemaVersion", currentSchemaVersion, nullptr);
}

juce::AudioProcessorValueTreeState::ParameterLayout ParameterState::createLayout()
{
    std::vector<std::unique_ptr<juce::RangedAudioParameter>> parameters;
    parameters.push_back(std::make_unique<juce::AudioParameterFloat>(juce::ParameterID { "drive", 1 }, "DRIVE", juce::NormalisableRange<float> { 0.0f, 18.0f, 0.01f }, 0.0f, "dB"));
    parameters.push_back(std::make_unique<juce::AudioParameterFloat>(juce::ParameterID { "ceiling", 1 }, "CEILING", juce::NormalisableRange<float> { -6.0f, 0.0f, 0.01f }, -1.0f, "dBFS"));
    juce::NormalisableRange<float> releaseRange { 10.0f, 1000.0f, 0.1f };
    releaseRange.setSkewForCentre(100.0f);
    parameters.push_back(std::make_unique<juce::AudioParameterFloat>(juce::ParameterID { "release", 1 }, "RELEASE", releaseRange, 100.0f, "ms"));
    parameters.push_back(std::make_unique<juce::AudioParameterBool>(juce::ParameterID { "bypass", 1 }, "BYPASS", false));
    parameters.push_back(std::make_unique<juce::AudioParameterChoice>(juce::ParameterID { "referencePlatform", 1 }, "REFERENCE", juce::StringArray { "Spotify", "Apple Music", "Amazon Music" }, 0));
    return { parameters.begin(), parameters.end() };
}

void ParameterState::setParameter(const char* id, float plainValue)
{
    if (auto* parameter = apvts.getParameter(id))
        parameter->setValueNotifyingHost(parameter->convertTo0to1(plainValue));
}
}
