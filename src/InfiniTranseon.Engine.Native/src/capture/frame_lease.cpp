#include "infini/capture/frame_lease.hpp"

#include <stdexcept>
#include <utility>

namespace infini::capture {

class frame_lease_state final {
public:
    frame_lease_state(frame_identity identity, std::function<void()> callback)
        : identity_(identity), close_callback_(std::move(callback)) {}

    void acquire() noexcept { tickets_.fetch_add(1U, std::memory_order_relaxed); }
    void release_ticket() noexcept {
        if (tickets_.fetch_sub(1U, std::memory_order_acq_rel) == 1U) try_close();
    }
    void release_root() noexcept {
        root_active_.store(false, std::memory_order_release);
        try_close();
    }
    [[nodiscard]] frame_identity identity() const noexcept { return identity_; }
    [[nodiscard]] bool closed() const noexcept { return closed_.load(std::memory_order_acquire); }
    [[nodiscard]] std::uint32_t tickets() const noexcept { return tickets_.load(std::memory_order_acquire); }

private:
    void try_close() noexcept {
        if (root_active_.load(std::memory_order_acquire) || tickets() != 0U) return;
        bool expected = false;
        if (!closed_.compare_exchange_strong(expected, true, std::memory_order_acq_rel)) return;
        try { close_callback_(); } catch (...) { }
    }

    frame_identity identity_{};
    std::function<void()> close_callback_;
    std::atomic<std::uint32_t> tickets_{};
    std::atomic<bool> root_active_{true};
    std::atomic<bool> closed_{};
};

gpu_use_ticket::gpu_use_ticket(std::shared_ptr<frame_lease_state> state) noexcept
    : state_(std::move(state)), status_(status::pending) {
    if (state_) state_->acquire();
}

gpu_use_ticket::~gpu_use_ticket() {
    if (status_ == status::pending) static_cast<void>(cancel());
}

gpu_use_ticket::gpu_use_ticket(gpu_use_ticket&& other) noexcept
    : state_(std::move(other.state_)), status_(std::exchange(other.status_, status::empty)) {}

gpu_use_ticket& gpu_use_ticket::operator=(gpu_use_ticket&& other) noexcept {
    if (this == &other) return *this;
    if (status_ == status::pending) static_cast<void>(cancel());
    state_ = std::move(other.state_);
    status_ = std::exchange(other.status_, status::empty);
    return *this;
}

bool gpu_use_ticket::submit() noexcept {
    if (status_ != status::pending) return false;
    status_ = status::submitted;
    return true;
}

bool gpu_use_ticket::complete() noexcept {
    if (status_ != status::submitted || !state_) return false;
    status_ = status::terminal;
    state_->release_ticket();
    state_.reset();
    return true;
}

bool gpu_use_ticket::cancel() noexcept {
    if (status_ != status::pending || !state_) return false;
    status_ = status::terminal;
    state_->release_ticket();
    state_.reset();
    return true;
}

bool gpu_use_ticket::valid() const noexcept {
    return status_ == status::pending || status_ == status::submitted;
}

frame_lease::frame_lease(frame_identity identity, std::function<void()> close_callback) {
    if (identity.capture_source_key == 0U || identity.frame_sequence == 0U ||
        identity.device_epoch == 0U || identity.frame_lease_id == 0U || !close_callback)
        throw std::invalid_argument("invalid frame lease");
    state_ = std::make_shared<frame_lease_state>(identity, std::move(close_callback));
}

frame_lease::~frame_lease() { release_root(); }
gpu_use_ticket frame_lease::acquire_ticket() { return gpu_use_ticket{state_}; }
void frame_lease::release_root() noexcept { if (state_) state_->release_root(); }
frame_identity frame_lease::identity() const noexcept { return state_->identity(); }
bool frame_lease::closed() const noexcept { return state_->closed(); }
std::uint32_t frame_lease::outstanding_tickets() const noexcept { return state_->tickets(); }

bool crop_lease::valid(const std::size_t maximum_bytes) const noexcept {
    if (source.capture_source_key == 0U || source.frame_sequence == 0U || source.device_epoch == 0U ||
        source.frame_lease_id == 0U || crop_lease_id == 0U || width == 0U || height == 0U ||
        stride < width || immutable_pixels.empty() || immutable_pixels.size() > maximum_bytes) return false;
    return immutable_pixels.size() == static_cast<std::size_t>(stride) * height;
}

} // namespace infini::capture
