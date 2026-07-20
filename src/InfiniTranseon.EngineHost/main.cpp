#include <infini_engine.h>
#include <infini_runtime_server.h>

#include <charconv>
#include <cstdint>
#include <iostream>
#include <string_view>

int main(const int argument_count, const char* const* const arguments)
{
    if (argument_count == 2 && std::string_view(arguments[1]) == "--abi-version")
    {
        std::cout << IT_EngineGetAbiVersion() << '\n';
        return 0;
    }

    constexpr std::string_view bootstrap_argument = "--bootstrap-handle=";
    if (argument_count == 2)
    {
        const std::string_view argument(arguments[1]);
        if (argument.starts_with(bootstrap_argument))
        {
            const std::string_view value_text = argument.substr(bootstrap_argument.size());
            std::uintptr_t value{};
            const auto [end, error] = std::from_chars(
                value_text.data(), value_text.data() + value_text.size(), value);
            if (error == std::errc{} && end == value_text.data() + value_text.size() &&
                value != 0U)
            {
                const auto handle = reinterpret_cast<HANDLE>(value);
                DWORD flags{};
                if (GetHandleInformation(handle, &flags) != FALSE)
                {
                    return static_cast<int>(infini::runtime::run_server(handle));
                }
            }
        }
    }

    std::cerr << "Invalid EngineHost startup arguments.\n";
    return 64;
}
