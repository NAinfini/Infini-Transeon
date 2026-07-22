#include "infini/capture/window_target_inspector.hpp"

#ifdef _WIN32

#include <dwmapi.h>
#include <shobjidl.h>
#include <wrl/client.h>

#include <array>
#include <vector>

namespace infini::capture {

window_inspection_result inspect_window_target(const HWND window) noexcept {
    if (window == nullptr || IsWindow(window) == FALSE)
        return {window_inspection_status::closed, std::nullopt};

    DWORD process_id{};
    if (GetWindowThreadProcessId(window, &process_id) == 0U || process_id == 0U)
        return {window_inspection_status::access_denied, std::nullopt};
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
    if (process == nullptr) return {window_inspection_status::access_denied, std::nullopt};
    std::vector<wchar_t> executable(32'768U);
    DWORD executable_length = static_cast<DWORD>(executable.size());
    const BOOL path_result = QueryFullProcessImageNameW(
        process, 0U, executable.data(), &executable_length);
    CloseHandle(process);
    if (path_result == FALSE) return {window_inspection_status::access_denied, std::nullopt};

    std::array<wchar_t, 256U> class_name{};
    const int class_length = GetClassNameW(
        window, class_name.data(), static_cast<int>(class_name.size()));
    if (class_length <= 0) return {window_inspection_status::access_denied, std::nullopt};
    RECT client{};
    POINT origin{};
    if (GetClientRect(window, &client) == FALSE || ClientToScreen(window, &origin) == FALSE)
        return {window_inspection_status::access_denied, std::nullopt};
    const LONG width = client.right - client.left;
    const LONG height = client.bottom - client.top;
    RECT screen_client{origin.x, origin.y, origin.x + width, origin.y + height};

    BOOL cloaked = FALSE;
    if (FAILED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))))
        cloaked = FALSE;
    Microsoft::WRL::ComPtr<IVirtualDesktopManager> desktops;
    HRESULT desktop_result = CoCreateInstance(
        CLSID_VirtualDesktopManager,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_PPV_ARGS(&desktops));
    BOOL current_desktop = FALSE;
    if (SUCCEEDED(desktop_result))
        desktop_result = desktops->IsWindowOnCurrentVirtualDesktop(window, &current_desktop);

    window_target_snapshot snapshot{
        window,
        process_id,
        std::wstring(executable.data(), executable_length),
        std::wstring(class_name.data(), static_cast<std::size_t>(class_length)),
        screen_client,
        MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST),
        GetDpiForWindow(window),
        IsWindowVisible(window) != FALSE,
        IsIconic(window) != FALSE,
        cloaked != FALSE,
        SUCCEEDED(desktop_result) && current_desktop != FALSE,
        GetForegroundWindow() == window,
    };
    return {
        SUCCEEDED(desktop_result)
            ? window_inspection_status::available
            : window_inspection_status::virtual_desktop_unavailable,
        std::move(snapshot),
    };
}

} // namespace infini::capture

#endif
