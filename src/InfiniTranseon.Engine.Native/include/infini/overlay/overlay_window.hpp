#pragma once

#include <cstdint>

#ifdef _WIN32
#include <windows.h>
#endif

namespace infini::overlay {

enum class overlay_window_error : std::uint8_t {
    none,
    wrong_thread,
    class_registration_failed,
    window_creation_failed,
    capture_exclusion_failed,
};

struct target_visibility final {
    bool target_visible{};
    bool minimized{};
    bool cloaked{};
    bool on_current_virtual_desktop{};
};

[[nodiscard]] bool should_show_overlay(target_visibility visibility) noexcept;

#ifdef _WIN32
class overlay_window final {
public:
    overlay_window() noexcept;
    ~overlay_window();
    overlay_window(const overlay_window&) = delete;
    overlay_window& operator=(const overlay_window&) = delete;

    [[nodiscard]] overlay_window_error create(RECT bounds) noexcept;
    [[nodiscard]] overlay_window_error set_bounds(RECT bounds) noexcept;
    [[nodiscard]] overlay_window_error set_visible(bool visible) noexcept;
    [[nodiscard]] bool pump_once() noexcept;
    [[nodiscard]] HWND handle() const noexcept;
    [[nodiscard]] bool capture_exclusion_active() const noexcept;
    static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) noexcept;

private:
    [[nodiscard]] bool on_owner_thread() const noexcept;

    DWORD owner_thread_{};
    HWND window_{};
    bool capture_exclusion_{};
};
#endif

} // namespace infini::overlay
