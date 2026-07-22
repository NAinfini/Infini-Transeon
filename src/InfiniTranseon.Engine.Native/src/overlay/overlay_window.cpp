#include "infini/overlay/overlay_window.hpp"

#ifdef _WIN32
#include <mutex>

namespace {
constexpr wchar_t window_class_name[] = L"InfiniTranseon.OverlayWindow";
std::once_flag registration_once;
ATOM window_class{};

void register_window_class() noexcept {
    WNDCLASSEXW value{};
    value.cbSize = sizeof(value);
    value.style = CS_HREDRAW | CS_VREDRAW;
    value.lpfnWndProc = infini::overlay::overlay_window::window_proc;
    value.hInstance = GetModuleHandleW(nullptr);
    value.hCursor = LoadCursorW(nullptr, MAKEINTRESOURCEW(32512));
    value.lpszClassName = window_class_name;
    window_class = RegisterClassExW(&value);
    if (window_class == 0U && GetLastError() == ERROR_CLASS_ALREADY_EXISTS) window_class = 1U;
}
}
#endif

namespace infini::overlay {

bool should_show_overlay(const target_visibility visibility) noexcept {
    return visibility.target_visible && !visibility.minimized && !visibility.cloaked &&
        visibility.on_current_virtual_desktop;
}

#ifdef _WIN32
overlay_window::overlay_window() noexcept : owner_thread_(GetCurrentThreadId()) {}

overlay_window::~overlay_window() {
    if (window_ != nullptr && on_owner_thread()) DestroyWindow(window_);
}

overlay_window_error overlay_window::create(const RECT bounds) noexcept {
    if (!on_owner_thread()) return overlay_window_error::wrong_thread;
    if (window_ != nullptr) return set_bounds(bounds);
    std::call_once(registration_once, register_window_class);
    if (window_class == 0U) return overlay_window_error::class_registration_failed;
    window_ = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
        window_class_name,
        L"",
        WS_POPUP,
        bounds.left,
        bounds.top,
        bounds.right - bounds.left,
        bounds.bottom - bounds.top,
        nullptr,
        nullptr,
        GetModuleHandleW(nullptr),
        this);
    if (window_ == nullptr) return overlay_window_error::window_creation_failed;
    SetLayeredWindowAttributes(window_, 0U, 255U, LWA_ALPHA);
    if (SetWindowDisplayAffinity(window_, WDA_EXCLUDEFROMCAPTURE) == FALSE) {
        DestroyWindow(window_);
        window_ = nullptr;
        return overlay_window_error::capture_exclusion_failed;
    }
    DWORD affinity{};
    capture_exclusion_ = GetWindowDisplayAffinity(window_, &affinity) != FALSE &&
        affinity == WDA_EXCLUDEFROMCAPTURE;
    if (!capture_exclusion_) {
        DestroyWindow(window_);
        window_ = nullptr;
        return overlay_window_error::capture_exclusion_failed;
    }
    return overlay_window_error::none;
}

overlay_window_error overlay_window::set_bounds(const RECT bounds) noexcept {
    if (!on_owner_thread()) return overlay_window_error::wrong_thread;
    if (window_ == nullptr) return overlay_window_error::window_creation_failed;
    return SetWindowPos(
        window_, HWND_TOPMOST, bounds.left, bounds.top,
        bounds.right - bounds.left, bounds.bottom - bounds.top,
        SWP_NOACTIVATE | SWP_NOOWNERZORDER) != FALSE
        ? overlay_window_error::none
        : overlay_window_error::window_creation_failed;
}

overlay_window_error overlay_window::set_visible(const bool visible) noexcept {
    if (!on_owner_thread()) return overlay_window_error::wrong_thread;
    if (window_ == nullptr) return overlay_window_error::window_creation_failed;
    ShowWindow(window_, visible ? SW_SHOWNOACTIVATE : SW_HIDE);
    return overlay_window_error::none;
}

bool overlay_window::pump_once() noexcept {
    if (!on_owner_thread()) return false;
    MSG message{};
    while (PeekMessageW(&message, nullptr, 0U, 0U, PM_REMOVE) != FALSE) {
        if (message.message == WM_QUIT) return false;
        TranslateMessage(&message);
        DispatchMessageW(&message);
    }
    return true;
}

HWND overlay_window::handle() const noexcept { return window_; }
bool overlay_window::capture_exclusion_active() const noexcept { return capture_exclusion_; }
bool overlay_window::on_owner_thread() const noexcept { return owner_thread_ == GetCurrentThreadId(); }

LRESULT CALLBACK overlay_window::window_proc(
    const HWND window,
    const UINT message,
    const WPARAM wparam,
    const LPARAM lparam) noexcept {
    if (message == WM_NCHITTEST) return HTTRANSPARENT;
    if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (message == WM_ERASEBKGND) return 1;
    return DefWindowProcW(window, message, wparam, lparam);
}
#endif

} // namespace infini::overlay
