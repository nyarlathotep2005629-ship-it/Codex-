#include "MeterWorker.h"

#include <algorithm>
#include <chrono>
#include <thread>

namespace hiphopmax::metering
{
MeterWorker::MeterWorker(MeterFIFO& fifo,
                         MeterSnapshotPublisher& publisher,
                         std::atomic<std::uint64_t>& meterResetGeneration,
                         std::atomic<std::uint64_t>& meterAppliedGeneration,
                         std::atomic<bool>& meterOverrun)
    : fifo_(fifo),
      publisher_(publisher),
      meterResetGeneration_(meterResetGeneration),
      meterAppliedGeneration_(meterAppliedGeneration),
      meterOverrun_(meterOverrun)
{
}

MeterWorker::~MeterWorker()
{
    stop();
}

bool MeterWorker::configure(unsigned long sampleRate, unsigned int channels)
{
    stop();
    sampleRate_ = sampleRate;
    channels_ = channels;
    processedFrames_.store(0, std::memory_order_relaxed);
    return meter_.configure(sampleRate_, channels_, true);
}

void MeterWorker::start()
{
    if (thread_.joinable())
        return;
    stopRequested_.store(false, std::memory_order_release);
    thread_ = std::thread([this] { run(); });
}

void MeterWorker::stop() noexcept
{
    stopRequested_.store(true, std::memory_order_release);
    if (thread_.joinable())
        thread_.join();
    running_.store(false, std::memory_order_release);
}

void MeterWorker::requestReset(std::uint64_t generation)
{
    meterResetGeneration_.store(generation, std::memory_order_release);
    publisher_.publishUnmeasured(generation);
}

void MeterWorker::applyGeneration(std::uint64_t generation) noexcept
{
    if (!meter_.reset())
    {
        publishOverrun(generation);
        return;
    }
    meterOverrun_.store(false, std::memory_order_release);
    processedFrames_.store(0, std::memory_order_relaxed);
    meterAppliedGeneration_.store(generation, std::memory_order_release);
    publisher_.publishUnmeasured(generation);
}

void MeterWorker::publishOverrun(std::uint64_t generation)
{
    MeterSnapshot snapshot;
    snapshot.generation = generation;
    snapshot.meterOverrun = true;
    snapshot.lufsIntegratedStatus = MeterFieldStatus::error;
    snapshot.lufsShortTermStatus = MeterFieldStatus::error;
    snapshot.lufsMomentaryStatus = MeterFieldStatus::error;
    snapshot.truePeakStatus = MeterFieldStatus::error;
    publisher_.publish(snapshot);
}

void MeterWorker::run() noexcept
{
    running_.store(true, std::memory_order_release);
    std::array<float, 4096> interleaved {};
    std::uint64_t framesSincePublish = 0;

    while (!stopRequested_.load(std::memory_order_acquire))
    {
        auto applied = meterAppliedGeneration_.load(std::memory_order_acquire);
        const auto requested = meterResetGeneration_.load(std::memory_order_acquire);
        if (requested > applied)
        {
            applyGeneration(requested);
            applied = requested;
        }

        if (meterOverrun_.load(std::memory_order_acquire))
        {
            publishOverrun(applied);
            MeterFrame discarded;
            while (fifo_.pop(discarded)) {}
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
            continue;
        }

        std::size_t frames = 0;
        MeterFrame frame;
        while (frames < interleaved.size() / 2 && fifo_.pop(frame))
        {
            if (frame.generation < applied)
                continue;
            if (frame.generation > applied)
            {
                applyGeneration(frame.generation);
                applied = frame.generation;
            }
            interleaved[frames * 2] = frame.l;
            interleaved[frames * 2 + 1] = frame.r;
            ++frames;
        }

        if (frames > 0)
        {
            if (!meter_.addInterleaved(interleaved.data(), frames))
            {
                publishOverrun(applied);
                meterOverrun_.store(true, std::memory_order_release);
                continue;
            }
            processedFrames_.fetch_add(frames, std::memory_order_relaxed);
            framesSincePublish += frames;
            if (framesSincePublish >= std::max<std::uint64_t>(1, sampleRate_ / 10))
            {
                publisher_.publish(meter_.measure(applied));
                framesSincePublish = 0;
            }
        }
        else
        {
            std::this_thread::sleep_for(std::chrono::milliseconds(1));
        }
    }

    const auto generation = meterAppliedGeneration_.load(std::memory_order_acquire);
    if (!meterOverrun_.load(std::memory_order_acquire) && meter_.totalFrames() > 0)
        publisher_.publish(meter_.measure(generation));
    running_.store(false, std::memory_order_release);
}
}
