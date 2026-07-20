#include "infini_runtime_server.h"

#include "infini_runtime_protocol.h"

#include <sddl.h>

#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <memory>
#include <span>
#include <string>
#include <vector>

namespace infini::runtime
{
namespace
{
constexpr std::size_t wire_header_bytes = 56U;
constexpr std::size_t handshake_request_payload_bytes = 40U;
constexpr std::size_t handshake_response_payload_bytes = 4U;
constexpr std::size_t maximum_bootstrap_bytes = 512U;
constexpr std::uint64_t filetime_to_datetime_ticks = 504'911'232'000'000'000ULL;

struct HandleCloser final
{
    void operator()(void* handle) const noexcept
    {
        if (handle != nullptr && handle != INVALID_HANDLE_VALUE)
        {
            CloseHandle(handle);
        }
    }
};

using unique_handle = std::unique_ptr<void, HandleCloser>;

struct LocalMemoryCloser final
{
    void operator()(void* memory) const noexcept
    {
        if (memory != nullptr)
        {
            LocalFree(memory);
        }
    }
};

using unique_local_memory = std::unique_ptr<void, LocalMemoryCloser>;

std::uint32_t read_u32(const std::span<const std::byte> bytes, const std::size_t offset) noexcept
{
    std::uint32_t value{};
    for (std::size_t index = 0; index < sizeof(value); ++index)
    {
        value |= std::to_integer<std::uint32_t>(bytes[offset + index]) << (index * 8U);
    }
    return value;
}

void write_u32(const std::span<std::byte> bytes, const std::size_t offset,
    const std::uint32_t value) noexcept
{
    for (std::size_t index = 0; index < sizeof(value); ++index)
    {
        bytes[offset + index] = static_cast<std::byte>((value >> (index * 8U)) & 0xffU);
    }
}

bool read_exact(HANDLE handle, const std::span<std::byte> destination) noexcept
{
    std::size_t completed{};
    while (completed < destination.size())
    {
        const DWORD requested = static_cast<DWORD>(std::min<std::size_t>(
            destination.size() - completed,
            std::numeric_limits<DWORD>::max()));
        DWORD transferred{};
        if (!ReadFile(handle, destination.data() + completed, requested, &transferred, nullptr) ||
            transferred == 0U)
        {
            return false;
        }
        completed += transferred;
    }
    return true;
}

bool write_exact(HANDLE handle, const std::span<const std::byte> source) noexcept
{
    std::size_t completed{};
    while (completed < source.size())
    {
        const DWORD requested = static_cast<DWORD>(std::min<std::size_t>(
            source.size() - completed,
            std::numeric_limits<DWORD>::max()));
        DWORD transferred{};
        if (!WriteFile(handle, source.data() + completed, requested, &transferred, nullptr) ||
            transferred == 0U)
        {
            return false;
        }
        completed += transferred;
    }
    return true;
}

std::uint64_t utc_ticks() noexcept
{
    FILETIME file_time{};
    GetSystemTimePreciseAsFileTime(&file_time);
    const ULARGE_INTEGER value{file_time.dwLowDateTime, file_time.dwHighDateTime};
    return value.QuadPart + filetime_to_datetime_ticks;
}

bool read_bootstrap(HANDLE handle, std::vector<std::byte>& payload) noexcept
{
    std::array<std::byte, sizeof(std::uint32_t)> prefix{};
    if (!read_exact(handle, prefix))
    {
        return false;
    }
    const std::uint32_t length = read_u32(prefix, 0U);
    if (length == 0U || length > maximum_bootstrap_bytes)
    {
        return false;
    }
    payload.resize(length);
    return read_exact(handle, payload);
}

bool current_user_security_attributes(
    SECURITY_ATTRIBUTES& attributes,
    unique_local_memory& security_descriptor) noexcept
{
    unique_handle token;
    HANDLE raw_token{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &raw_token))
    {
        return false;
    }
    token.reset(raw_token);

    DWORD token_bytes{};
    GetTokenInformation(token.get(), TokenUser, nullptr, 0U, &token_bytes);
    if (token_bytes == 0U)
    {
        return false;
    }
    std::vector<std::byte> token_buffer(token_bytes);
    if (!GetTokenInformation(token.get(), TokenUser, token_buffer.data(), token_bytes,
        &token_bytes))
    {
        return false;
    }

    const auto* token_user = reinterpret_cast<const TOKEN_USER*>(token_buffer.data());
    wchar_t* raw_sid{};
    if (!ConvertSidToStringSidW(token_user->User.Sid, &raw_sid))
    {
        return false;
    }
    unique_local_memory sid_string(raw_sid);
    const std::wstring sddl = std::wstring(L"D:P(A;;GA;;;") + raw_sid + L")";

    PSECURITY_DESCRIPTOR raw_descriptor{};
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
        sddl.c_str(), SDDL_REVISION_1, &raw_descriptor, nullptr))
    {
        return false;
    }
    security_descriptor.reset(raw_descriptor);
    attributes.nLength = sizeof(attributes);
    attributes.lpSecurityDescriptor = raw_descriptor;
    attributes.bInheritHandle = FALSE;
    return true;
}

unique_handle create_pipe(const BootstrapConfig& config) noexcept
{
    SECURITY_ATTRIBUTES attributes{};
    unique_local_memory descriptor;
    if (!current_user_security_attributes(attributes, descriptor))
    {
        return {};
    }

    const std::wstring pipe_name = L"\\\\.\\pipe\\" +
        std::wstring(config.pipe_name.begin(), config.pipe_name.end());
    return unique_handle(CreateNamedPipeW(
        pipe_name.c_str(),
        PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1U,
        64U * 1024U,
        64U * 1024U,
        0U,
        &attributes));
}

bool authenticate_and_respond(HANDLE pipe, BootstrapConfig& config) noexcept
{
    std::array<std::byte, sizeof(std::uint32_t)> prefix{};
    if (!read_exact(pipe, prefix))
    {
        return false;
    }
    const std::uint32_t body_length = read_u32(prefix, 0U);
    if (body_length != wire_header_bytes + handshake_request_payload_bytes)
    {
        return false;
    }

    std::array<std::byte, wire_header_bytes + handshake_request_payload_bytes> request{};
    if (!read_exact(pipe, request))
    {
        return false;
    }
    const auto request_span = std::span<const std::byte>(request);
    ULONG client_process_id{};
    if (GetNamedPipeClientProcessId(pipe, &client_process_id) == FALSE)
    {
        client_process_id = 0U;
    }
    const bool authenticated = authenticate_handshake(
        request_span, client_process_id, GetCurrentProcessId(), config, utc_ticks());
    SecureZeroMemory(request.data() + 64U, nonce_bytes);
    if (!authenticated)
    {
        return false;
    }

    std::array<std::byte, sizeof(std::uint32_t) + wire_header_bytes +
        handshake_response_payload_bytes> response{};
    write_u32(response, 0U, wire_header_bytes + handshake_response_payload_bytes);
    write_u32(response, 4U, wire_magic);
    write_u32(response, 8U, protocol_version);
    write_u32(response, 12U, handshake_response_kind);
    std::ranges::copy(request_span.subspan(12U, 16U), response.begin() + 16U);
    std::ranges::copy(config.runtime_epoch, response.begin() + 32U);
    std::ranges::copy(request_span.subspan(44U, 8U), response.begin() + 48U);
    write_u32(response, 56U, handshake_response_payload_bytes);
    write_u32(response, 60U, GetCurrentProcessId());
    return write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
}
}

ServerExitCode run_server(HANDLE bootstrap_read_handle) noexcept
{
    unique_handle bootstrap_handle(bootstrap_read_handle);
    std::vector<std::byte> bootstrap_payload;
    if (!read_bootstrap(bootstrap_handle.get(), bootstrap_payload))
    {
        SecureZeroMemory(bootstrap_payload.data(), bootstrap_payload.size());
        return ServerExitCode::invalid_bootstrap;
    }
    bootstrap_handle.reset();

    auto parsed = parse_bootstrap(bootstrap_payload);
    SecureZeroMemory(bootstrap_payload.data(), bootstrap_payload.size());
    if (!parsed.has_value())
    {
        return ServerExitCode::invalid_bootstrap;
    }
    BootstrapConfig config = parsed.take_value();

    unique_handle pipe = create_pipe(config);
    if (!pipe || pipe.get() == INVALID_HANDLE_VALUE)
    {
        return ServerExitCode::pipe_creation_failed;
    }
    const BOOL connected = ConnectNamedPipe(pipe.get(), nullptr);
    if (!connected && GetLastError() != ERROR_PIPE_CONNECTED)
    {
        return ServerExitCode::pipe_creation_failed;
    }
    if (!authenticate_and_respond(pipe.get(), config))
    {
        DisconnectNamedPipe(pipe.get());
        return ServerExitCode::authentication_failed;
    }

    std::byte ignored{};
    DWORD transferred{};
    while (ReadFile(pipe.get(), &ignored, 1U, &transferred, nullptr) && transferred != 0U)
    {
    }
    DisconnectNamedPipe(pipe.get());
    return ServerExitCode::success;
}
}
