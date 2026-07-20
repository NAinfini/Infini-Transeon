#pragma once

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <Windows.h>

namespace infini::runtime
{
enum class ServerExitCode : int
{
    success = 0,
    invalid_arguments = 64,
    invalid_bootstrap = 65,
    pipe_creation_failed = 66,
    authentication_failed = 67,
    protocol_failed = 68,
};

[[nodiscard]] ServerExitCode run_server(HANDLE bootstrap_read_handle) noexcept;
}
