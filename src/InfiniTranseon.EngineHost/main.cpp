#include <infini_engine.h>

#include <iostream>
#include <string_view>

int main(const int argument_count, const char* const* const arguments)
{
    if (argument_count == 2 && std::string_view(arguments[1]) == "--abi-version")
    {
        std::cout << IT_EngineGetAbiVersion() << '\n';
        return 0;
    }

    std::cerr << "EngineHost startup is not available until the authenticated IPC bootstrap is implemented.\n";
    return 64;
}
