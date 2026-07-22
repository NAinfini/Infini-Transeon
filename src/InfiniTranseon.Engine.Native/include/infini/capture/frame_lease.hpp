#pragma once

#include <atomic>
#include <cstdint>
#include <functional>
#include <memory>
#include <mutex>
#include <optional>
#include <vector>

namespace infini::capture {

struct frame_identity final {
    std::uint64_t capture_source_key{};
    std::uint64_t frame_sequence{};
    std::uint64_t device_epoch{};
    std::uint64_t frame_lease_id{};
};

class frame_lease_state;

class gpu_use_ticket final {
public:
    gpu_use_ticket() noexcept = default;
    explicit gpu_use_ticket(std::shared_ptr<frame_lease_state> state) noexcept;
    ~gpu_use_ticket();
    gpu_use_ticket(gpu_use_ticket&& other) noexcept;
    gpu_use_ticket& operator=(gpu_use_ticket&& other) noexcept;
    gpu_use_ticket(const gpu_use_ticket&) = delete;
    gpu_use_ticket& operator=(const gpu_use_ticket&) = delete;

    [[nodiscard]] bool submit() noexcept;
    [[nodiscard]] bool complete() noexcept;
    [[nodiscard]] bool cancel() noexcept;
    [[nodiscard]] bool valid() const noexcept;

private:
    enum class status : std::uint8_t { empty, pending, submitted, terminal };
    std::shared_ptr<frame_lease_state> state_;
    status status_{status::empty};
};

class frame_lease final {
public:
    frame_lease(frame_identity identity, std::function<void()> close_callback);
    ~frame_lease();
    frame_lease(const frame_lease&) = delete;
    frame_lease& operator=(const frame_lease&) = delete;

    [[nodiscard]] gpu_use_ticket acquire_ticket();
    void release_root() noexcept;
    [[nodiscard]] frame_identity identity() const noexcept;
    [[nodiscard]] bool closed() const noexcept;
    [[nodiscard]] std::uint32_t outstanding_tickets() const noexcept;

private:
    std::shared_ptr<frame_lease_state> state_;
};

struct crop_lease final {
    frame_identity source{};
    std::uint64_t crop_lease_id{};
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t stride{};
    std::vector<std::byte> immutable_pixels;

    [[nodiscard]] bool valid(std::size_t maximum_bytes) const noexcept;
};

} // namespace infini::capture
