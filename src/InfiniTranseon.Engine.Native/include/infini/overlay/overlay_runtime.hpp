#pragma once

#ifdef _WIN32

#include "infini/overlay/overlay_renderer.hpp"
#include "infini/overlay/overlay_window.hpp"

#include <d3d11.h>
#include <windows.h>

#include <cstdint>
#include <functional>
#include <memory>
#include <optional>

namespace infini::overlay {

enum class overlay_runtime_status : std::uint8_t {
    applied,
    superseded,
    invalid_state,
    capture_exclusion_failed,
    device_failed,
    background_unavailable,
    stopped,
};

struct overlay_runtime_event final {
    overlay_runtime_status status{};
    std::uint64_t revision{};
};

struct overlay_target_state final {
    RECT bounds{};
    target_visibility visibility{};
};

class overlay_runtime final {
public:
    using event_callback = std::function<void(overlay_runtime_event)>;
    using target_state_provider = std::function<std::optional<overlay_target_state>()>;

    overlay_runtime(
        ID3D11Device* device,
        RECT initial_bounds,
        event_callback callback,
        target_state_provider target_provider = {});
    ~overlay_runtime();
    overlay_runtime(const overlay_runtime&) = delete;
    overlay_runtime& operator=(const overlay_runtime&) = delete;

    [[nodiscard]] bool start();
    [[nodiscard]] bool submit(std::shared_ptr<const desired_state> state);
    [[nodiscard]] bool update_target(RECT bounds, target_visibility visibility);
    void stop() noexcept;

private:
    struct implementation;
    std::shared_ptr<implementation> impl_;
};

} // namespace infini::overlay

#endif
