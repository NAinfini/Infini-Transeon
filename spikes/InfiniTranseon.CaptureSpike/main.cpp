#include <windows.h>
#include <appmodel.h>
#include <dxgi1_2.h>

#include <wrl/client.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <winrt/Windows.Graphics.Capture.h>

#include <iostream>
#include <iomanip>
#include <string>
#include <string_view>
#include <vector>

namespace
{
constexpr int success = 0;
constexpr int invalid_usage = 64;
constexpr int no_package_identity = 65;
constexpr int access_not_allowed = 66;
constexpr int hotkey_unavailable = 67;
constexpr int capture_exclusion_unavailable = 68;
constexpr int platform_error = 70;
constexpr int probe_hotkey_id = 0x494E;

LONG get_package_full_name(std::wstring& package_full_name)
{
    UINT32 length = 0;
    LONG result = GetCurrentPackageFullName(&length, nullptr);
    if (result != ERROR_INSUFFICIENT_BUFFER)
    {
        return result;
    }

    std::vector<wchar_t> buffer(length);
    result = GetCurrentPackageFullName(&length, buffer.data());
    if (result == ERROR_SUCCESS)
    {
        package_full_name.assign(buffer.data());
    }

    return result;
}

int report_package_identity()
{
    std::wstring package_full_name;
    const LONG result = get_package_full_name(package_full_name);
    if (result == APPMODEL_ERROR_NO_PACKAGE)
    {
        std::cerr << "packageIdentity=absent\n";
        return no_package_identity;
    }

    if (result != ERROR_SUCCESS)
    {
        std::cerr << "packageIdentity=error win32=" << result << '\n';
        return platform_error;
    }

    std::wcout << L"packageIdentity=present fullName=" << package_full_name << L'\n';
    return success;
}

std::string_view access_status_name(
    const winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus status) noexcept
{
    using winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus;
    switch (status)
    {
    case AppCapabilityAccessStatus::Allowed:
        return "allowed";
    case AppCapabilityAccessStatus::DeniedBySystem:
        return "denied-by-system";
    case AppCapabilityAccessStatus::NotDeclaredByApp:
        return "not-declared-by-app";
    case AppCapabilityAccessStatus::DeniedByUser:
        return "denied-by-user";
    case AppCapabilityAccessStatus::UserPromptRequired:
        return "user-prompt-required";
    default:
        return "unknown";
    }
}

int request_borderless_access()
{
    if (report_package_identity() != success)
    {
        std::cerr << "borderlessAccess=not-requested reason=package-identity-required\n";
        return no_package_identity;
    }

    try
    {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        using namespace winrt::Windows::Graphics::Capture;
        using winrt::Windows::Security::Authorization::AppCapabilityAccess::AppCapabilityAccessStatus;
        const AppCapabilityAccessStatus status = GraphicsCaptureAccess::RequestAccessAsync(
            GraphicsCaptureAccessKind::Borderless).get();
        std::cout << "borderlessAccess=" << access_status_name(status) << '\n';
        return status == AppCapabilityAccessStatus::Allowed ? success : access_not_allowed;
    }
    catch (const winrt::hresult_error& error)
    {
        std::wcerr << L"borderlessAccess=error hresult=0x" << std::hex
                   << static_cast<unsigned long>(error.code().value) << L'\n';
        return platform_error;
    }
}

LRESULT CALLBACK probe_window_proc(
    const HWND window,
    const UINT message,
    const WPARAM wparam,
    const LPARAM lparam)
{
    return DefWindowProcW(window, message, wparam, lparam);
}

int probe_capture_exclusion()
{
    constexpr wchar_t class_name[] = L"InfiniTranseonCaptureExclusionProbe";
    const HINSTANCE instance = GetModuleHandleW(nullptr);
    WNDCLASSW window_class{};
    window_class.lpfnWndProc = probe_window_proc;
    window_class.hInstance = instance;
    window_class.lpszClassName = class_name;
    window_class.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
    const ATOM registered = RegisterClassW(&window_class);
    if (registered == 0 && GetLastError() != ERROR_CLASS_ALREADY_EXISTS)
    {
        std::cerr << "captureExclusion=error stage=register-window win32=" << GetLastError() << '\n';
        return platform_error;
    }

    const HWND window = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST,
        class_name,
        L"Infini-Transeon capture exclusion probe",
        WS_POPUP,
        100,
        100,
        480,
        160,
        nullptr,
        nullptr,
        instance,
        nullptr);
    if (window == nullptr)
    {
        std::cerr << "captureExclusion=error stage=create-window win32=" << GetLastError() << '\n';
        return platform_error;
    }
    SetLayeredWindowAttributes(window, RGB(0, 0, 0), 220, LWA_ALPHA);
    ShowWindow(window, SW_SHOWNOACTIVATE);
    UpdateWindow(window);
    if (!SetWindowDisplayAffinity(window, WDA_EXCLUDEFROMCAPTURE))
    {
        const DWORD error = GetLastError();
        DestroyWindow(window);
        std::cerr << "captureExclusion=unavailable stage=set-affinity win32=" << error << '\n';
        return capture_exclusion_unavailable;
    }
    DWORD affinity = WDA_NONE;
    if (!GetWindowDisplayAffinity(window, &affinity) || affinity != WDA_EXCLUDEFROMCAPTURE)
    {
        const DWORD error = GetLastError();
        DestroyWindow(window);
        std::cerr << "captureExclusion=unavailable stage=verify-affinity win32=" << error << '\n';
        return capture_exclusion_unavailable;
    }

    std::cout << "captureExclusion=api-verified affinity=0x" << std::hex << affinity << std::dec
              << " manualObservationSeconds=15\n";
    std::cout << "Capture this display and a window target now; the black probe rectangle must be absent.\n";
    const ULONGLONG deadline = GetTickCount64() + 15'000ULL;
    MSG message{};
    while (GetTickCount64() < deadline)
    {
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        Sleep(10);
    }
    DestroyWindow(window);
    std::cout << "captureExclusion=manual-observation-required\n";
    return success;
}

int probe_global_hotkey()
{
    if (!RegisterHotKey(nullptr, probe_hotkey_id, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_F10))
    {
        std::cerr << "globalHotkey=unavailable win32=" << GetLastError() << '\n';
        return hotkey_unavailable;
    }
    std::cout << "globalHotkey=registered chord=Ctrl+Alt+F10 timeoutSeconds=30\n";
    const ULONGLONG deadline = GetTickCount64() + 30'000ULL;
    int result = hotkey_unavailable;
    MSG message{};
    while (GetTickCount64() < deadline)
    {
        const DWORD remaining = static_cast<DWORD>(deadline - GetTickCount64());
        const DWORD wait = MsgWaitForMultipleObjectsEx(
            0, nullptr, remaining, QS_ALLINPUT, MWMO_INPUTAVAILABLE);
        if (wait == WAIT_TIMEOUT) break;
        if (wait == WAIT_FAILED)
        {
            std::cerr << "globalHotkey=error win32=" << GetLastError() << '\n';
            result = platform_error;
            break;
        }
        while (PeekMessageW(&message, nullptr, 0, 0, PM_REMOVE))
        {
            if (message.message == WM_HOTKEY && message.wParam == probe_hotkey_id)
            {
                std::cout << "globalHotkey=received\n";
                result = success;
                break;
            }
            TranslateMessage(&message);
            DispatchMessageW(&message);
        }
        if (result == success) break;
    }
    UnregisterHotKey(nullptr, probe_hotkey_id);
    if (result == hotkey_unavailable) std::cerr << "globalHotkey=timeout\n";
    return result;
}

int report_adapter_inventory()
{
    Microsoft::WRL::ComPtr<IDXGIFactory1> factory;
    const HRESULT factory_result = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
    if (FAILED(factory_result))
    {
        std::cerr << "adapterInventory=error hresult=0x" << std::hex
                  << static_cast<unsigned long>(factory_result) << std::dec << '\n';
        return platform_error;
    }
    UINT adapter_index = 0;
    for (;; ++adapter_index)
    {
        Microsoft::WRL::ComPtr<IDXGIAdapter1> adapter;
        const HRESULT adapter_result = factory->EnumAdapters1(adapter_index, &adapter);
        if (adapter_result == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(adapter_result)) return platform_error;
        DXGI_ADAPTER_DESC1 description{};
        if (FAILED(adapter->GetDesc1(&description))) return platform_error;
        std::wcout << L"adapter index=" << adapter_index
                   << L" luid=" << std::hex << description.AdapterLuid.HighPart << L':'
                   << description.AdapterLuid.LowPart << std::dec
                   << L" vendor=0x" << std::hex << description.VendorId << std::dec
                   << L" description=\"" << description.Description << L"\"\n";
        UINT output_index = 0;
        for (;; ++output_index)
        {
            Microsoft::WRL::ComPtr<IDXGIOutput> output;
            const HRESULT output_result = adapter->EnumOutputs(output_index, &output);
            if (output_result == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(output_result)) return platform_error;
            DXGI_OUTPUT_DESC output_description{};
            if (FAILED(output->GetDesc(&output_description))) return platform_error;
            const RECT rect = output_description.DesktopCoordinates;
            std::wcout << L" output index=" << output_index
                       << L" device=" << output_description.DeviceName
                       << L" rect=" << rect.left << L',' << rect.top << L','
                       << rect.right << L',' << rect.bottom
                       << L" attached=" << (output_description.AttachedToDesktop ? L"true" : L"false")
                       << L'\n';
        }
    }
    std::cout << "adapterInventory=complete adapters=" << adapter_index << '\n';
    return adapter_index == 0 ? platform_error : success;
}

void print_help()
{
    std::cout
        << "InfiniTranseon.CaptureSpike\n"
        << "  --package-identity          Report whether the process has package identity.\n"
        << "  --request-borderless       Request Borderless capture access; may show a system prompt.\n"
        << "  --capture-exclusion       Verify WDA_EXCLUDEFROMCAPTURE and show a 15-second manual probe.\n"
        << "  --hotkey-probe            Wait 30 seconds for Ctrl+Alt+F10.\n"
        << "  --adapter-inventory       Report DXGI adapters and attached outputs.\n"
        << "  --help                     Show this help.\n";
}
}

int main(const int argument_count, const char* const* const arguments)
{
    if (argument_count != 2)
    {
        print_help();
        return invalid_usage;
    }

    const std::string_view command(arguments[1]);
    if (command == "--help")
    {
        print_help();
        return success;
    }

    if (command == "--package-identity")
    {
        return report_package_identity();
    }

    if (command == "--request-borderless")
    {
        return request_borderless_access();
    }

    if (command == "--capture-exclusion") return probe_capture_exclusion();
    if (command == "--hotkey-probe") return probe_global_hotkey();
    if (command == "--adapter-inventory") return report_adapter_inventory();

    std::cerr << "Unknown capture-spike command. Use --help.\n";
    return invalid_usage;
}
