#include <windows.h>
#include <appmodel.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Security.Authorization.AppCapabilityAccess.h>
#include <winrt/Windows.Graphics.Capture.h>

#include <iostream>
#include <string>
#include <string_view>
#include <vector>

namespace
{
constexpr int success = 0;
constexpr int invalid_usage = 64;
constexpr int no_package_identity = 65;
constexpr int access_not_allowed = 66;
constexpr int platform_error = 70;

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

void print_help()
{
    std::cout
        << "InfiniTranseon.CaptureSpike\n"
        << "  --package-identity          Report whether the process has package identity.\n"
        << "  --request-borderless       Request Borderless capture access; may show a system prompt.\n"
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

    std::cerr << "Unknown capture-spike command. Use --help.\n";
    return invalid_usage;
}
