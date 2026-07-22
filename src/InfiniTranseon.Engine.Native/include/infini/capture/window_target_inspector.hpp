#pragma once

#ifdef _WIN32

#include <windows.h>

#include <cstdint>
#include <optional>
#include <string>

namespace infini::capture {

enum class window_inspection_status : std::uint8_t {
    available,
    closed,
    access_denied,
    virtual_desktop_unavailable,
};

struct window_target_snapshot final {
    HWND window{};
    std::uint32_t process_id{};
    std::wstring executable_path;
    std::wstring window_class;
    RECT client_screen_pixels{};
    HMONITOR monitor{};
    std::uint32_t dpi{};
    bool visible{};
    bool minimized{};
    bool cloaked{};
    bool on_current_virtual_desktop{};
    bool foreground{};
};

struct window_inspection_result final {
    window_inspection_status status{window_inspection_status::closed};
    std::optional<window_target_snapshot> snapshot;
};

[[nodiscard]] window_inspection_result inspect_window_target(HWND window) noexcept;

} // namespace infini::capture

#endif
