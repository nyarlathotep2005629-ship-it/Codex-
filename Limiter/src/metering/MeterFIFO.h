#pragma once

#include "MeterTypes.h"

#include <atomic>
#include <cstddef>
#include <vector>

namespace hiphopmax::metering
{
class MeterFIFO
{
public:
    explicit MeterFIFO(std::size_t capacity = 262144)
        : storage_(capacity + 1)
    {
    }

    [[nodiscard]] bool push(const MeterFrame& frame) noexcept
    {
        const auto head = head_.load(std::memory_order_relaxed);
        const auto next = increment(head);
        if (next == tail_.load(std::memory_order_acquire))
            return false;
        storage_[head] = frame;
        head_.store(next, std::memory_order_release);
        return true;
    }

    [[nodiscard]] bool pop(MeterFrame& frame) noexcept
    {
        const auto tail = tail_.load(std::memory_order_relaxed);
        if (tail == head_.load(std::memory_order_acquire))
            return false;
        frame = storage_[tail];
        tail_.store(increment(tail), std::memory_order_release);
        return true;
    }

    void clear() noexcept
    {
        tail_.store(head_.load(std::memory_order_acquire), std::memory_order_release);
    }

    [[nodiscard]] bool empty() const noexcept
    {
        return tail_.load(std::memory_order_acquire) == head_.load(std::memory_order_acquire);
    }

    [[nodiscard]] std::size_t capacity() const noexcept { return storage_.size() - 1; }

private:
    [[nodiscard]] std::size_t increment(std::size_t value) const noexcept
    {
        ++value;
        return value == storage_.size() ? 0 : value;
    }

    std::vector<MeterFrame> storage_;
    alignas(64) std::atomic<std::size_t> head_ { 0 };
    alignas(64) std::atomic<std::size_t> tail_ { 0 };
};
}
