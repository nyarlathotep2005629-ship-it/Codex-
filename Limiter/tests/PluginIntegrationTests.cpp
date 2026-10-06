#include "PluginProcessor.h"

#include <juce_events/juce_events.h>

#include <algorithm>
#include <chrono>
#include <cmath>
#include <iostream>
#include <limits>
#include <numbers>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

namespace
{
void require(bool condition, const std::string& message)
{
    if (!condition)
        throw std::runtime_error(message);
}

void setParameter(HipHopMaxAudioProcessor& processor, const char* id, float plainValue)
{
    auto* parameter = processor.parameters().apvts.getParameter(id);
    require(parameter != nullptr, std::string("missing parameter ") + id);
    parameter->setValueNotifyingHost(parameter->convertTo0to1(plainValue));
}

int impulsePosition(const juce::AudioBuffer<float>& buffer, float threshold = 0.25f)
{
    for (int i = 0; i < buffer.getNumSamples(); ++i)
        if (std::abs(buffer.getSample(0, i)) >= threshold)
            return i;
    return -1;
}

void testBusPrecisionAndIdentity()
{
    HipHopMaxAudioProcessor processor;
    juce::AudioProcessor::BusesLayout stereo;
    stereo.inputBuses.add(juce::AudioChannelSet::stereo());
    stereo.outputBuses.add(juce::AudioChannelSet::stereo());
    require(processor.isBusesLayoutSupported(stereo), "INT-003 stereo rejected");
    juce::AudioProcessor::BusesLayout mono;
    mono.inputBuses.add(juce::AudioChannelSet::mono());
    mono.outputBuses.add(juce::AudioChannelSet::mono());
    require(!processor.isBusesLayoutSupported(mono), "INT-003 mono accepted");
    require(!processor.supportsDoublePrecisionProcessing(), "INT-004 double precision accepted");
    require(processor.getName() == "HipHopMax", "INT-009 name mismatch");
    require(processor.getBypassParameter() == processor.parameters().apvts.getParameter("bypass"), "INT-008 bypass parameter is not APVTS source");
}

void testLatencyBypassAndCeiling()
{
    for (const double rate : { 44100.0, 48000.0, 88200.0, 96000.0 })
    {
        HipHopMaxAudioProcessor processor;
        setParameter(processor, "ceiling", -1.0f);
        processor.prepareToPlay(rate, 1024);
        const int latency = static_cast<int>(std::round(rate * 0.005));
        require(processor.getLatencySamples() == latency, "INT-008 latency report mismatch");
        juce::AudioBuffer<float> wet(2, latency + 64);
        wet.clear();
        wet.setSample(0, 0, 1.5f);
        wet.setSample(1, 0, 1.5f);
        juce::MidiBuffer midi;
        processor.processBlock(wet, midi);
        require(impulsePosition(wet) == latency, "DSP-003 plugin impulse latency mismatch");
        const float ceiling = std::pow(10.0f, -0.99f / 20.0f);
        require(std::abs(wet.getSample(0, latency)) <= ceiling, "DSP-004 plugin ceiling exceeded");
        processor.releaseResources();

        HipHopMaxAudioProcessor bypassed;
        setParameter(bypassed, "drive", 12.0f);
        setParameter(bypassed, "ceiling", -6.0f);
        setParameter(bypassed, "bypass", 1.0f);
        bypassed.prepareToPlay(rate, 1024);
        juce::AudioBuffer<float> dry(2, latency + 64);
        dry.clear();
        dry.setSample(0, 0, 0.75f);
        dry.setSample(1, 0, 0.75f);
        bypassed.processBlockBypassed(dry, midi);
        require(impulsePosition(dry) == latency, "INT-008 processBlockBypassed latency mismatch");
        require(std::abs(dry.getSample(0, latency) - 0.75f) < 1.0e-5f, "DSP-016 plugin dry contaminated");
        require(bypassed.getLatencySamples() == latency, "INT-008 latency changed in bypass");
        bypassed.releaseResources();
    }
}

juce::AudioBuffer<float> render(bool nonRealtime)
{
    HipHopMaxAudioProcessor processor;
    processor.setNonRealtime(nonRealtime);
    setParameter(processor, "drive", 6.0f);
    setParameter(processor, "ceiling", -1.0f);
    processor.prepareToPlay(48000.0, 511);
    juce::AudioBuffer<float> result(2, 12000);
    for (int i = 0; i < result.getNumSamples(); ++i)
    {
        const auto x = 0.7f * std::sin(static_cast<float>(2.0 * std::numbers::pi * 997.0 * i / 48000.0));
        result.setSample(0, i, x);
        result.setSample(1, i, x);
    }
    juce::MidiBuffer midi;
    for (int offset = 0; offset < result.getNumSamples(); offset += 511)
    {
        const int count = std::min(511, result.getNumSamples() - offset);
        juce::AudioBuffer<float> view(result.getArrayOfWritePointers(), 2, offset, count);
        processor.processBlock(view, midi);
    }
    processor.releaseResources();
    return result;
}

void testOfflineEquality()
{
    const auto realtime = render(false);
    const auto offline = render(true);
    for (int channel = 0; channel < 2; ++channel)
        for (int i = 0; i < realtime.getNumSamples(); ++i)
            require(realtime.getSample(channel, i) == offline.getSample(channel, i), "INT-005 offline output mismatch");
}

void testStateSchema()
{
    HipHopMaxAudioProcessor source;
    setParameter(source, "drive", 7.25f);
    setParameter(source, "ceiling", -2.0f);
    setParameter(source, "release", 333.0f);
    setParameter(source, "bypass", 1.0f);
    setParameter(source, "referencePlatform", 2.0f);
    juce::MemoryBlock state;
    source.getStateInformation(state);
    const auto xmlText = juce::String::fromUTF8(static_cast<const char*>(state.getData()), static_cast<int>(state.getSize()));
    auto xml = juce::parseXML(xmlText);
    require(xml != nullptr && static_cast<int>(xml->getIntAttribute("schemaVersion")) == 1, "INT-002 schemaVersion missing");

    HipHopMaxAudioProcessor restored;
    restored.setStateInformation(state.getData(), static_cast<int>(state.getSize()));
    require(std::abs(restored.parameters().driveDb() - 7.25f) < 0.02f, "INT-002 drive restore");
    require(std::abs(restored.parameters().ceilingDb() + 2.0f) < 0.02f, "INT-002 ceiling restore");
    require(restored.parameters().bypass(), "INT-002 bypass restore");

    xml->removeAttribute("schemaVersion");
    const auto schema0Text = xml->toString();
    HipHopMaxAudioProcessor migrated;
    migrated.setStateInformation(schema0Text.toRawUTF8(), schema0Text.getNumBytesAsUTF8());
    require(std::abs(migrated.parameters().driveDb() - 7.25f) < 0.02f, "INT-002 schema0 migration");

    xml->setAttribute("schemaVersion", 99);
    const auto futureText = xml->toString();
    HipHopMaxAudioProcessor fallback;
    setParameter(fallback, "drive", 12.0f);
    fallback.setStateInformation(futureText.toRawUTF8(), futureText.getNumBytesAsUTF8());
    require(std::abs(fallback.parameters().driveDb()) < 0.001f
            && std::abs(fallback.parameters().ceilingDb() + 1.0f) < 0.001f,
            "INT-002 unknown schema did not fallback");
}

bool waitUntil(const auto& predicate, std::chrono::milliseconds timeout = std::chrono::milliseconds(5000))
{
    const auto deadline = std::chrono::steady_clock::now() + timeout;
    while (std::chrono::steady_clock::now() < deadline)
    {
        if (predicate()) return true;
        std::this_thread::sleep_for(std::chrono::milliseconds(2));
    }
    return false;
}

void testMeterLifecycleAndEditor()
{
    HipHopMaxAudioProcessor processor;
    processor.prepareToPlay(48000.0, 512);
    require(waitUntil([&] { return processor.isMeterWorkerRunning(); }), "MTR-016 worker did not start");
    const auto generation = processor.meterResetGeneration();
    {
        std::unique_ptr<juce::AudioProcessorEditor> editor(processor.createEditor());
        require(editor != nullptr, "PoC editor creation failed");
    }
    require(processor.isMeterWorkerRunning() && processor.meterResetGeneration() == generation,
            "MTR-016 editor close changed worker lifecycle");

    juce::AudioBuffer<float> block(2, 512);
    juce::MidiBuffer midi;
    for (int blockIndex = 0; blockIndex < 300; ++blockIndex)
    {
        for (int i = 0; i < block.getNumSamples(); ++i)
        {
            const auto absolute = blockIndex * 512 + i;
            const auto x = 0.1f * std::sin(static_cast<float>(2.0 * std::numbers::pi * 1000.0 * absolute / 48000.0));
            block.setSample(0, i, x);
            block.setSample(1, i, x);
        }
        processor.processBlock(block, midi);
    }
    hiphopmax::metering::MeterSnapshot snapshot;
    require(waitUntil([&] { return processor.readMeterSnapshot(snapshot)
                                && snapshot.lufsIntegratedStatus == hiphopmax::metering::MeterFieldStatus::valid; }),
            "Meter integration did not publish valid LUFS");
    require(std::isfinite(snapshot.lufsIntegrated) && snapshot.generation == generation, "Meter snapshot invalid");

    processor.resetMeterSession();
    hiphopmax::metering::MeterSnapshot reset = snapshot;
    require(processor.readMeterSnapshot(reset)
            && reset.generation == generation + 1
            && reset.lufsIntegratedStatus == hiphopmax::metering::MeterFieldStatus::unmeasured,
            "MTR-011 processor reset not immediate");

    for (int i = 0; i < 20; ++i)
    {
        processor.prepareToPlay(i % 2 == 0 ? 44100.0 : 96000.0, i % 2 == 0 ? 17 : 1024);
        require(waitUntil([&] { return processor.isMeterWorkerRunning(); }), "MTR-016 reprepare start failed");
    }
    processor.releaseResources();
    require(!processor.isMeterWorkerRunning(), "MTR-016 release left worker alive");
}

template <typename Function>
void run(const char* ids, Function&& function)
{
    function();
    std::cout << ids << " PASS\n";
}
}

int main()
{
    try
    {
        juce::ScopedJuceInitialiser_GUI initialiseJuce;
        run("INT-003/INT-004/INT-008/INT-009", testBusPrecisionAndIdentity);
        run("DSP-003/DSP-004/DSP-016/INT-008", testLatencyBypassAndCeiling);
        run("INT-005", testOfflineEquality);
        run("INT-002", testStateSchema);
        run("MTR-005/MTR-011/MTR-016", testMeterLifecycleAndEditor);
    }
    catch (const std::exception& error)
    {
        std::cerr << "FAIL: " << error.what() << '\n';
        return 1;
    }
    return 0;
}
