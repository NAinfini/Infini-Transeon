#pragma once

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <map>
#include <mutex>
#include <optional>

namespace infini::ocr
{
using readback_clock = std::chrono::steady_clock;
using readback_ticket = std::uint64_t;

class readback_ring;

class mapped_readback_lease final
{
public:
    mapped_readback_lease() = default;
    mapped_readback_lease(const mapped_readback_lease&) = delete;
    mapped_readback_lease& operator=(const mapped_readback_lease&) = delete;
    mapped_readback_lease(mapped_readback_lease&& other) noexcept;
    mapped_readback_lease& operator=(mapped_readback_lease&& other) noexcept;
    ~mapped_readback_lease();

    [[nodiscard]] std::size_t byte_count() const noexcept { return byte_count_; }
    [[nodiscard]] bool within_hold_limit(readback_clock::time_point now) const noexcept;

private:
    friend class readback_ring;
    mapped_readback_lease(
        readback_ring* owner,
        readback_ticket ticket,
        std::size_t byte_count,
        readback_clock::time_point mapped_at,
        std::chrono::milliseconds maximum_hold) noexcept;
    void release() noexcept;

    readback_ring* owner_{};
    readback_ticket ticket_{};
    std::size_t byte_count_{};
    readback_clock::time_point mapped_at_{};
    std::chrono::milliseconds maximum_hold_{};
};

class readback_ring final
{
public:
    readback_ring(
        std::size_t maximum_slots,
        std::size_t maximum_bytes,
        std::size_t maximum_mapped,
        std::size_t maximum_copy_bytes_per_dispatch,
        std::chrono::milliseconds maximum_hold);

    std::optional<readback_ticket> reserve(std::size_t byte_count);
    void mark_fence_complete(readback_ticket ticket);
    std::optional<mapped_readback_lease> try_map(
        readback_ticket ticket,
        readback_clock::time_point now);
    bool cancel(readback_ticket ticket);

private:
    friend class mapped_readback_lease;
    struct slot_state
    {
        std::size_t byte_count{};
        bool fence_complete{};
        bool mapped{};
    };
    void release_mapped(readback_ticket ticket) noexcept;

    std::size_t maximum_slots_{};
    std::size_t maximum_bytes_{};
    std::size_t maximum_mapped_{};
    std::size_t maximum_copy_bytes_per_dispatch_{};
    std::chrono::milliseconds maximum_hold_{};
    std::mutex mutex_{};
    std::map<readback_ticket, slot_state> slots_{};
    readback_ticket next_ticket_{1U};
    std::size_t committed_bytes_{};
    std::size_t mapped_count_{};
};
}
