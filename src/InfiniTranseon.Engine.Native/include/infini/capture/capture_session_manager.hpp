#pragma once

#ifdef _WIN32

#include "infini/capture/capture_targets.hpp"
#include "infini/capture/windows_capture_source.hpp"

#include <functional>
#include <memory>
#include <mutex>

namespace infini::capture {

class capture_session_manager final {
public:
    using source_factory =
        std::function<std::unique_ptr<capture_source>(capture_source_descriptor)>;

    explicit capture_session_manager(source_factory factory);
    ~capture_session_manager();
    capture_session_manager(const capture_session_manager&) = delete;
    capture_session_manager& operator=(const capture_session_manager&) = delete;

    [[nodiscard]] bool attach(
        std::uint64_t logical_target_id,
        physical_source_identity identity,
        capture_source_descriptor descriptor,
        capture_source::frame_callback on_frame,
        capture_source::lifecycle_callback on_lifecycle);
    [[nodiscard]] bool detach(std::uint64_t logical_target_id) noexcept;
    void stop_all() noexcept;
    [[nodiscard]] std::size_t physical_source_count() const noexcept;
    [[nodiscard]] std::size_t logical_target_count(physical_source_identity identity) const noexcept;

private:
    struct shared_state;

    source_factory factory_;
    std::shared_ptr<shared_state> state_;
    mutable std::mutex control_gate_;
};

} // namespace infini::capture

#endif
