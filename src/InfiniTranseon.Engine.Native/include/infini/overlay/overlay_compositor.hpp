#pragma once

#ifdef _WIN32

#include "infini/overlay/overlay_types.hpp"

#include <d3d11.h>
#include <windows.h>

#include <cstdint>
#include <memory>

namespace infini::overlay {

enum class compositor_error : std::uint8_t {
    none,
    wrong_thread,
    invalid_argument,
    device_creation_failed,
    composition_creation_failed,
    drawing_failed,
    commit_failed,
};

class overlay_compositor final {
public:
    overlay_compositor() noexcept;
    ~overlay_compositor();
    overlay_compositor(const overlay_compositor&) = delete;
    overlay_compositor& operator=(const overlay_compositor&) = delete;

    [[nodiscard]] compositor_error initialize(
        HWND overlay_window,
        ID3D11Device* device,
        std::uint32_t width,
        std::uint32_t height) noexcept;
    [[nodiscard]] compositor_error resize(std::uint32_t width, std::uint32_t height) noexcept;
    [[nodiscard]] compositor_error render(
        const desired_state& state,
        float dpi_scale,
        const desired_state* previous = nullptr,
        float refinement_progress = 1.0F,
        std::uint32_t transition_milliseconds = 0U) noexcept;
    void reset() noexcept;

private:
    struct implementation;
    [[nodiscard]] bool on_owner_thread() const noexcept;

    DWORD owner_thread_{};
    std::unique_ptr<implementation> impl_;
};

} // namespace infini::overlay

#endif
