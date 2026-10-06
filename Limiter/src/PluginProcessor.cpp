#include "PluginProcessor.h"

#include <juce_audio_utils/juce_audio_utils.h>

#include <cmath>

HipHopMaxAudioProcessor::HipHopMaxAudioProcessor()
    : AudioProcessor(BusesProperties()
                         .withInput("Input", juce::AudioChannelSet::stereo(), true)
                         .withOutput("Output", juce::AudioChannelSet::stereo(), true)),
      parameters_(*this),
      meterFifo_(262144),
      meterWorker_(meterFifo_, meterPublisher_, meterResetGeneration_, meterAppliedGeneration_, meterOverrun_)
{
}

HipHopMaxAudioProcessor::~HipHopMaxAudioProcessor()
{
    meterFifoWriteEnabled_.store(false, std::memory_order_release);
    meterWorker_.stop();
}

void HipHopMaxAudioProcessor::prepareToPlay(double sampleRate, int maximumExpectedSamplesPerBlock)
{
    meterFifoWriteEnabled_.store(false, std::memory_order_release);
    meterWorker_.stop();
    meterFifo_.clear();
    const auto generation = meterResetGeneration_.fetch_add(1, std::memory_order_acq_rel) + 1;
    maximizer_.prepare(sampleRate, maximumExpectedSamplesPerBlock, 2);
    setLatencySamples(maximizer_.getLatencySamples());
    if (meterWorker_.configure(static_cast<unsigned long>(std::llround(sampleRate)), 2))
    {
        meterAppliedGeneration_.store(generation, std::memory_order_release);
        meterOverrun_.store(false, std::memory_order_release);
        meterPublisher_.publishUnmeasured(generation);
        meterWorker_.start();
        meterFifoWriteEnabled_.store(true, std::memory_order_release);
    }
}

void HipHopMaxAudioProcessor::releaseResources()
{
    meterFifoWriteEnabled_.store(false, std::memory_order_release);
    meterWorker_.stop();
    meterFifo_.clear();
}

bool HipHopMaxAudioProcessor::isBusesLayoutSupported(const BusesLayout& layouts) const
{
    const auto stereo = juce::AudioChannelSet::stereo();
    return layouts.getMainInputChannelSet() == stereo
        && layouts.getMainOutputChannelSet() == stereo;
}

void HipHopMaxAudioProcessor::processBlock(juce::AudioBuffer<float>& buffer, juce::MidiBuffer&)
{
    processCommon(buffer, false);
}

void HipHopMaxAudioProcessor::processBlockBypassed(juce::AudioBuffer<float>& buffer, juce::MidiBuffer&)
{
    processCommon(buffer, true);
}

void HipHopMaxAudioProcessor::processCommon(juce::AudioBuffer<float>& buffer, bool forceHostBypass) noexcept
{
    juce::ScopedNoDenormals noDenormals;
    maximizer_.setDriveDb(parameters_.driveDb());
    maximizer_.setCeilingDb(parameters_.ceilingDb());
    maximizer_.setReleaseMs(parameters_.releaseMs());
    maximizer_.setBypass(forceHostBypass || parameters_.bypass());
    float* channels[] { buffer.getWritePointer(0), buffer.getWritePointer(1) };
    maximizer_.process(channels, 2, buffer.getNumSamples());

    if (!meterFifoWriteEnabled_.load(std::memory_order_acquire))
        return;
    const auto generation = meterResetGeneration_.load(std::memory_order_acquire);
    const auto* left = buffer.getReadPointer(0);
    const auto* right = buffer.getReadPointer(1);
    for (int i = 0; i < buffer.getNumSamples(); ++i)
    {
        if (!meterFifo_.push({ left[i], right[i], generation }))
        {
            meterOverrun_.store(true, std::memory_order_release);
            break;
        }
    }
}

juce::AudioProcessorParameter* HipHopMaxAudioProcessor::getBypassParameter() const { return parameters_.bypassParameter(); }
bool HipHopMaxAudioProcessor::supportsDoublePrecisionProcessing() const { return false; }

juce::AudioProcessorEditor* HipHopMaxAudioProcessor::createEditor()
{
    return new juce::GenericAudioProcessorEditor(*this);
}

bool HipHopMaxAudioProcessor::hasEditor() const { return true; }
const juce::String HipHopMaxAudioProcessor::getName() const { return "HipHopMax"; }
bool HipHopMaxAudioProcessor::acceptsMidi() const { return false; }
bool HipHopMaxAudioProcessor::producesMidi() const { return false; }
bool HipHopMaxAudioProcessor::isMidiEffect() const { return false; }
double HipHopMaxAudioProcessor::getTailLengthSeconds() const { return 0.005; }
int HipHopMaxAudioProcessor::getNumPrograms() { return 1; }
int HipHopMaxAudioProcessor::getCurrentProgram() { return 0; }
void HipHopMaxAudioProcessor::setCurrentProgram(int) {}
const juce::String HipHopMaxAudioProcessor::getProgramName(int index) { return index == 0 ? "Default" : juce::String {}; }
void HipHopMaxAudioProcessor::changeProgramName(int, const juce::String&) {}

void HipHopMaxAudioProcessor::getStateInformation(juce::MemoryBlock& destinationData) { parameters_.save(destinationData); }

void HipHopMaxAudioProcessor::setStateInformation(const void* data, int sizeInBytes)
{
    parameters_.restore(data, sizeInBytes);
    resetMeterSession();
}

void HipHopMaxAudioProcessor::resetMeterSession()
{
    const auto generation = meterResetGeneration_.fetch_add(1, std::memory_order_acq_rel) + 1;
    meterOverrun_.store(false, std::memory_order_release);
    meterWorker_.requestReset(generation);
}

bool HipHopMaxAudioProcessor::readMeterSnapshot(hiphopmax::metering::MeterSnapshot& snapshot) const noexcept
{
    auto candidate = snapshot;
    if (!meterPublisher_.read(candidate))
        return false;
    if (candidate.generation < meterResetGeneration_.load(std::memory_order_acquire))
        return false;
    snapshot = candidate;
    return true;
}

float HipHopMaxAudioProcessor::getGainReductionDb() const noexcept { return maximizer_.getGainReductionDb(); }

juce::AudioProcessor* JUCE_CALLTYPE createPluginFilter()
{
    return new HipHopMaxAudioProcessor();
}
