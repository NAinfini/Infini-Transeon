#include "infini_runtime_server.h"

#include "infini_runtime_protocol.h"
#include "runtime_capture_controller.h"
#include <infini_engine.h>

#include <sddl.h>
#include <objbase.h>
#include <psapi.h>

#include <algorithm>
#include <array>
#include <charconv>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <map>
#include <memory>
#include <mutex>
#include <span>
#include <string>
#include <string_view>
#include <vector>

namespace infini::runtime
{
namespace
{
constexpr std::size_t wire_header_bytes = 56U;
constexpr std::size_t handshake_request_payload_bytes = 40U;
constexpr std::size_t handshake_response_payload_bytes = 180U;
constexpr std::size_t maximum_bootstrap_bytes = 512U;
constexpr std::uint32_t maximum_message_bytes = 8'388'608U;
constexpr std::uint32_t control_request_kind = 2U;
constexpr std::uint32_t control_response_kind = 3U;
constexpr std::uint32_t shutdown_request_kind = 17U;
constexpr std::uint32_t shutdown_acknowledgement_kind = 18U;
constexpr std::uint32_t capture_acknowledgement_schema_version = 2U;
constexpr std::size_t capture_acknowledgement_fixed_payload_bytes = 60U;
constexpr std::size_t overlay_acknowledgement_fixed_payload_bytes = 48U;
constexpr std::size_t policy_acknowledgement_fixed_payload_bytes = 24U;
constexpr std::size_t processing_acknowledgement_fixed_payload_bytes = 40U;
constexpr std::size_t ocr_result_acknowledgement_fixed_payload_bytes = 48U;
constexpr std::uint64_t filetime_to_datetime_ticks = 504'911'232'000'000'000ULL;
constexpr std::uint64_t response_lifetime_ticks = 300'000'000ULL;

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

std::uint64_t read_u64(const std::span<const std::byte> bytes, const std::size_t offset) noexcept
{
    std::uint64_t value{};
    for (std::size_t index = 0; index < sizeof(value); ++index)
    {
        value |= std::to_integer<std::uint64_t>(bytes[offset + index]) << (index * 8U);
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

void write_u64(const std::span<std::byte> bytes, const std::size_t offset,
    const std::uint64_t value) noexcept
{
    for (std::size_t index = 0; index < sizeof(value); ++index)
    {
        bytes[offset + index] = static_cast<std::byte>((value >> (index * 8U)) & 0xffULL);
    }
}

bool write_capabilities(
    const std::span<std::byte> bytes,
    std::size_t offset) noexcept
{
    IT_RuntimeCapabilitiesV1 capabilities{};
    capabilities.struct_size = sizeof(capabilities);
    capabilities.abi_version = IT_ENGINE_ABI_VERSION;
    if (IT_EngineGetCapabilities(&capabilities) != IT_RESULT_OK)
    {
        return false;
    }

    const auto u32 = [&](const std::uint32_t value) noexcept
    {
        write_u32(bytes, offset, value);
        offset += sizeof(value);
    };
    const auto u64 = [&](const std::uint64_t value) noexcept
    {
        write_u64(bytes, offset, value);
        offset += sizeof(value);
    };

    u32(protocol_version);
    u32(capabilities.max_capture_sources);
    u32(capabilities.max_targets);
    u32(capabilities.max_capture_dimension);
    u64(capabilities.max_capture_pixels_per_source);
    u32(capabilities.max_regions_per_target);
    u32(capabilities.max_active_tracks_per_target);
    u32(capabilities.max_ocr_boxes_per_result);
    u32(capabilities.max_source_chars);
    u32(capabilities.max_overlay_chars_per_target);
    u32(capabilities.max_translation_channels_per_region);
    u32(capabilities.max_outstanding_wgc_frames_per_source);
    u32(capabilities.max_owned_frame_textures_per_source);
    u32(capabilities.max_readback_crops_per_source);
    u64(capabilities.max_readback_pixels_per_source_ring);
    u64(capabilities.max_global_ocr_crop_bytes_in_flight);
    u32(capabilities.max_mapped_readbacks_per_adapter);
    u32(capabilities.max_mapped_readback_hold_milliseconds);
    u64(capabilities.max_detection_pyramid_bytes_per_source);
    u64(capabilities.max_overlay_surface_bytes_per_target);
    u32(capabilities.max_ocr_sessions);
    u64(capabilities.max_ocr_tensor_workspace_bytes);
    u64(capabilities.max_engine_committed_bytes);
    u64(8'589'934'592ULL);
    u64(capabilities.max_gpu_bytes_per_adapter_ceiling);
    u32(capabilities.max_gpu_budget_percentage);
    u32(capabilities.max_ipc_message_bytes);
    u64(capabilities.max_ipc_in_flight_bytes);
    u64(5'242'880ULL);
    u64(536'870'912ULL);
    u64(67'108'864ULL);
    return true;
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
    if (!write_capabilities(response, 64U))
    {
        return false;
    }
    return write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
}

bool write_empty_response(
    HANDLE pipe,
    const std::span<const std::byte> request,
    const std::uint32_t response_kind,
    std::mutex& write_gate) noexcept
{
    std::array<std::byte, sizeof(std::uint32_t) + wire_header_bytes> response{};
    write_u32(response, 0U, wire_header_bytes);
    write_u32(response, 4U, wire_magic);
    write_u32(response, 8U, protocol_version);
    write_u32(response, 12U, response_kind);
    std::ranges::copy(request.subspan(12U, 40U), response.begin() + 16U);
    write_u64(response, 48U, utc_ticks() + response_lifetime_ticks);
    write_u32(response, 56U, 0U);
    std::scoped_lock lock(write_gate);
    return write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
}

bool write_capture_target_response(
    HANDLE pipe,
    const std::span<const std::byte> request,
    const CaptureTargetCommand& command,
    const CaptureTargetApplyResult& result,
    std::mutex& write_gate) noexcept
{
    const bool accepted = result.accepted;
    const std::string_view error_code = result.error_code;
    if ((accepted && !error_code.empty()) || (!accepted && error_code.empty()) ||
        error_code.size() > 128U)
        return false;
    const std::size_t payload_bytes = capture_acknowledgement_fixed_payload_bytes +
        error_code.size();
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload_bytes);
    const auto response_span = std::span<std::byte>(response);
    write_u32(response_span, 0U, static_cast<std::uint32_t>(
        wire_header_bytes + payload_bytes));
    write_u32(response_span, 4U, wire_magic);
    write_u32(response_span, 8U, protocol_version);
    write_u32(response_span, 12U, capture_target_acknowledgement_kind);
    std::ranges::copy(request.subspan(12U, 40U), response.begin() + 16U);
    write_u64(response_span, 48U, utc_ticks() + response_lifetime_ticks);
    write_u32(response_span, 56U, static_cast<std::uint32_t>(payload_bytes));

    constexpr std::size_t payload_offset = sizeof(std::uint32_t) + wire_header_bytes;
    write_u32(response_span, payload_offset, capture_acknowledgement_schema_version);
    response[payload_offset + 4U] = accepted ? std::byte{1} : std::byte{};
    write_u64(response_span, payload_offset + 8U, command.command_revision);
    std::ranges::copy(command.target_id, response.begin() + payload_offset + 16U);
    std::ranges::copy(command.target_instance_id,
        response.begin() + payload_offset + 32U);
    write_u32(response_span, payload_offset + 48U,
        result.lifecycle_state);
    write_u32(response_span, payload_offset + 52U,
        static_cast<std::uint32_t>(error_code.size()));
    write_u32(response_span, payload_offset + 56U,
        static_cast<std::uint32_t>(result.native_error_code));
    std::ranges::transform(error_code,
        response.begin() + payload_offset + capture_acknowledgement_fixed_payload_bytes,
        [](const char value) { return static_cast<std::byte>(value); });
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

bool write_overlay_response(
    HANDLE pipe,
    const std::span<const std::byte> request,
    const overlay::desired_state& state,
    const OverlayApplyResult& result,
    std::mutex& write_gate) noexcept
{
    if ((result.accepted && !result.error_code.empty()) ||
        (!result.accepted && result.error_code.empty()) ||
        result.error_code.size() > 128U)
        return false;
    const std::size_t payload_bytes = overlay_acknowledgement_fixed_payload_bytes +
        result.error_code.size();
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload_bytes);
    const auto bytes = std::span<std::byte>(response);
    write_u32(bytes, 0U, static_cast<std::uint32_t>(wire_header_bytes + payload_bytes));
    write_u32(bytes, 4U, wire_magic);
    write_u32(bytes, 8U, protocol_version);
    write_u32(bytes, 12U, overlay_acknowledgement_kind);
    std::ranges::copy(request.subspan(12U, 40U), response.begin() + 16U);
    write_u64(bytes, 48U, utc_ticks() + response_lifetime_ticks);
    write_u32(bytes, 56U, static_cast<std::uint32_t>(payload_bytes));
    constexpr std::size_t payload_offset = sizeof(std::uint32_t) + wire_header_bytes;
    write_u32(bytes, payload_offset, 1U);
    response[payload_offset + 4U] = result.accepted ? std::byte{1} : std::byte{};
    response[payload_offset + 5U] = static_cast<std::byte>(result.status);
    write_u64(bytes, payload_offset + 8U, state.revision);
    std::ranges::copy(state.target_instance, response.begin() + payload_offset + 16U);
    write_u32(bytes, payload_offset + 32U,
        static_cast<std::uint32_t>(result.error_code.size()));
    write_u32(bytes, payload_offset + 36U,
        static_cast<std::uint32_t>(result.native_error_code));
    std::ranges::transform(result.error_code,
        response.begin() + payload_offset + overlay_acknowledgement_fixed_payload_bytes,
        [](const char value) { return static_cast<std::byte>(value); });
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

struct RuntimePolicyState final
{
    struct ProfileState final
    {
        std::uint64_t revision{};
        std::uint64_t profile_revision{};
        std::vector<PolicyRegionState> regions;
    };
    std::map<std::array<std::byte, 16U>, ProfileState> profiles;

    [[nodiscard]] std::string apply(PolicyRevisionCommand command)
    {
        const auto current = profiles.find(command.profile_id);
        if (current != profiles.end())
        {
            if (command.revision <= current->second.revision)
                return "runtime.policy.staleRevision";
            if (command.profile_revision < current->second.profile_revision)
                return "runtime.policy.staleProfile";
        }
        for (const PolicyRegionState& region : command.regions)
        {
            std::array<bool, 6U> actions{};
            std::size_t offset{};
            while (offset < region.policy.size())
            {
                const std::size_t end = region.policy.find(';', offset);
                const std::size_t length = end == std::string::npos
                    ? region.policy.size() - offset
                    : end - offset;
                const std::string_view item(region.policy.data() + offset, length);
                if (item.size() < 3U || item[1U] != ':' || item.front() < '0' ||
                    item.front() > '5')
                    return "runtime.policy.invalidAction";
                const std::size_t action = static_cast<std::size_t>(item.front() - '0');
                if (actions[action]) return "runtime.policy.duplicateAction";
                actions[action] = true;
                const std::string_view state = item.substr(2U);
                if (state != "configured" && state != "degraded" && state != "paused")
                    return "runtime.policy.invalidState";
                if (action == 0U && state != "paused")
                    return "runtime.policy.invalidState";
                if (end == std::string::npos) break;
                if (end + 1U == region.policy.size())
                    return "runtime.policy.invalidAction";
                offset = end + 1U;
            }
        }
        profiles.insert_or_assign(command.profile_id, ProfileState{
            command.revision,
            command.profile_revision,
            std::move(command.regions),
        });
        return {};
    }
};

bool write_policy_response(
    HANDLE pipe,
    const std::span<const std::byte> request,
    const PolicyRevisionCommand& command,
    const std::string_view rejection,
    std::mutex& write_gate) noexcept
{
    if (rejection.size() > 128U) return false;
    const bool accepted = rejection.empty();
    const std::size_t payload_bytes = policy_acknowledgement_fixed_payload_bytes +
        rejection.size();
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload_bytes);
    const auto bytes = std::span<std::byte>(response);
    write_u32(bytes, 0U, static_cast<std::uint32_t>(wire_header_bytes + payload_bytes));
    write_u32(bytes, 4U, wire_magic);
    write_u32(bytes, 8U, protocol_version);
    write_u32(bytes, 12U, policy_acknowledgement_kind);
    std::ranges::copy(request.subspan(12U, 40U), response.begin() + 16U);
    write_u64(bytes, 48U, utc_ticks() + response_lifetime_ticks);
    write_u32(bytes, 56U, static_cast<std::uint32_t>(payload_bytes));
    constexpr std::size_t payload_offset = sizeof(std::uint32_t) + wire_header_bytes;
    write_u32(bytes, payload_offset, 1U);
    response[payload_offset + 4U] = accepted ? std::byte{1} : std::byte{};
    write_u64(bytes, payload_offset + 8U, command.revision);
    write_u32(bytes, payload_offset + 16U,
        static_cast<std::uint32_t>(rejection.size()));
    std::ranges::transform(rejection,
        response.begin() + payload_offset + policy_acknowledgement_fixed_payload_bytes,
        [](const char value) { return static_cast<std::byte>(value); });
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

bool write_processing_response(
    HANDLE pipe,
    const std::span<const std::byte> request,
    const ProcessingConfigurationCommand& command,
    const ProcessingApplyResult& result,
    std::mutex& write_gate) noexcept
{
    if ((result.accepted && !result.error_code.empty()) ||
        (!result.accepted && result.error_code.empty()) ||
        result.error_code.size() > 128U)
        return false;
    const std::size_t payload_bytes = processing_acknowledgement_fixed_payload_bytes +
        result.error_code.size();
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload_bytes);
    const auto bytes = std::span<std::byte>(response);
    write_u32(bytes, 0U, static_cast<std::uint32_t>(wire_header_bytes + payload_bytes));
    write_u32(bytes, 4U, wire_magic);
    write_u32(bytes, 8U, protocol_version);
    write_u32(bytes, 12U, processing_configuration_acknowledgement_kind);
    std::ranges::copy(request.subspan(12U, 40U), response.begin() + 16U);
    write_u64(bytes, 48U, utc_ticks() + response_lifetime_ticks);
    write_u32(bytes, 56U, static_cast<std::uint32_t>(payload_bytes));
    constexpr std::size_t payload_offset = sizeof(std::uint32_t) + wire_header_bytes;
    write_u32(bytes, payload_offset, 1U);
    response[payload_offset + 4U] = result.accepted ? std::byte{1} : std::byte{};
    response[payload_offset + 5U] = static_cast<std::byte>(result.status);
    write_u64(bytes, payload_offset + 8U, command.configuration_revision);
    std::ranges::copy(command.target_instance_id,
        response.begin() + payload_offset + 16U);
    write_u32(bytes, payload_offset + 32U,
        static_cast<std::uint32_t>(result.error_code.size()));
    std::ranges::transform(result.error_code,
        response.begin() + payload_offset + processing_acknowledgement_fixed_payload_bytes,
        [](const char value) { return static_cast<std::byte>(value); });
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

bool write_ocr_result_response(
    HANDLE pipe,
    const std::span<const std::byte> request,
    const OcrResultCommand& command,
    const OcrResultApplyResult& result,
    std::mutex& write_gate) noexcept
{
    if ((result.accepted && !result.error_code.empty()) ||
        (!result.accepted && result.error_code.empty()) ||
        result.error_code.size() > 128U)
        return false;
    const std::size_t payload_bytes = ocr_result_acknowledgement_fixed_payload_bytes +
        result.error_code.size();
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload_bytes);
    const auto bytes = std::span<std::byte>(response);
    write_u32(bytes, 0U, static_cast<std::uint32_t>(wire_header_bytes + payload_bytes));
    write_u32(bytes, 4U, wire_magic);
    write_u32(bytes, 8U, protocol_version);
    write_u32(bytes, 12U, ocr_result_acknowledgement_kind);
    std::ranges::copy(request.subspan(12U, 40U), response.begin() + 16U);
    write_u64(bytes, 48U, utc_ticks() + response_lifetime_ticks);
    write_u32(bytes, 56U, static_cast<std::uint32_t>(payload_bytes));
    constexpr std::size_t payload_offset = sizeof(std::uint32_t) + wire_header_bytes;
    write_u32(bytes, payload_offset, 1U);
    write_u32(bytes, payload_offset + 4U, result.status);
    write_u64(bytes, payload_offset + 8U, command.token.source_generation);
    write_u64(bytes, payload_offset + 16U, command.token.result_sequence);
    std::ranges::copy(command.token.target_instance_id,
        response.begin() + payload_offset + 24U);
    write_u32(bytes, payload_offset + 40U,
        static_cast<std::uint32_t>(result.error_code.size()));
    std::ranges::transform(result.error_code,
        response.begin() + payload_offset + ocr_result_acknowledgement_fixed_payload_bytes,
        [](const char value) { return static_cast<std::byte>(value); });
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

bool write_ocr_result_event(
    HANDLE pipe,
    const BootstrapConfig& config,
    const std::span<const std::byte> payload,
    std::mutex& write_gate) noexcept
{
    if (payload.empty() || payload.size() > maximum_message_bytes - wire_header_bytes)
        return false;
    std::vector<std::byte> event(
        sizeof(std::uint32_t) + wire_header_bytes + payload.size());
    const auto bytes = std::span<std::byte>(event);
    write_u32(bytes, 0U, static_cast<std::uint32_t>(wire_header_bytes + payload.size()));
    write_u32(bytes, 4U, wire_magic);
    write_u32(bytes, 8U, protocol_version);
    write_u32(bytes, 12U, ocr_result_kind);
    GUID event_id{};
    if (FAILED(CoCreateGuid(&event_id))) return false;
    std::ranges::copy(std::as_bytes(std::span{&event_id, 1U}), event.begin() + 16U);
    std::ranges::copy(config.runtime_epoch, event.begin() + 32U);
    write_u64(bytes, 48U, utc_ticks() + response_lifetime_ticks);
    write_u32(bytes, 56U, static_cast<std::uint32_t>(payload.size()));
    std::ranges::copy(payload, event.begin() + sizeof(std::uint32_t) + wire_header_bytes);
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, event) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(event.data(), event.size());
    return written;
}

bool write_native_ocr_result_event(
    HANDLE pipe,
    const BootstrapConfig& config,
    const OcrResultCommand& command,
    std::mutex& write_gate) noexcept
{
    auto payload = encode_ocr_result(command);
    if (!payload.has_value()) return false;
    const bool written = write_ocr_result_event(
        pipe, config, *payload, write_gate);
    SecureZeroMemory(payload->data(), payload->size());
    return written;
}

bool write_target_lifecycle_event(
    HANDLE pipe,
    const BootstrapConfig& config,
    const CaptureTargetLifecycleEvent& event,
    std::mutex& write_gate) noexcept
{
    constexpr std::uint32_t target_lifecycle_kind = 5U;
    constexpr std::uint32_t lifecycle_schema_version = 1U;
    constexpr std::size_t lifecycle_fixed_payload_bytes = 76U;
    if (event.lifecycle_sequence == 0U || event.pixel_width == 0U ||
        event.pixel_height == 0U || event.dpi == 0U || event.error_code.size() > 128U)
        return false;
    const std::size_t payload_bytes = lifecycle_fixed_payload_bytes +
        event.error_code.size();
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload_bytes);
    const auto response_span = std::span<std::byte>(response);
    write_u32(response_span, 0U, static_cast<std::uint32_t>(
        wire_header_bytes + payload_bytes));
    write_u32(response_span, 4U, wire_magic);
    write_u32(response_span, 8U, protocol_version);
    write_u32(response_span, 12U, target_lifecycle_kind);
    GUID event_id{};
    if (FAILED(CoCreateGuid(&event_id))) return false;
    const auto event_id_bytes = std::as_bytes(std::span{&event_id, 1U});
    std::ranges::copy(event_id_bytes, response.begin() + 16U);
    std::ranges::copy(config.runtime_epoch, response.begin() + 32U);
    const std::uint64_t now = utc_ticks();
    write_u64(response_span, 48U, now + response_lifetime_ticks);
    write_u32(response_span, 56U, static_cast<std::uint32_t>(payload_bytes));

    constexpr std::size_t payload_offset = sizeof(std::uint32_t) + wire_header_bytes;
    write_u32(response_span, payload_offset, lifecycle_schema_version);
    write_u32(response_span, payload_offset + 4U, event.lifecycle_state);
    write_u64(response_span, payload_offset + 8U, event.lifecycle_sequence);
    std::ranges::copy(event.target_id, response.begin() + payload_offset + 16U);
    std::ranges::copy(event.target_instance_id,
        response.begin() + payload_offset + 32U);
    write_u64(response_span, payload_offset + 48U, now);
    write_u32(response_span, payload_offset + 56U, event.pixel_width);
    write_u32(response_span, payload_offset + 60U, event.pixel_height);
    write_u32(response_span, payload_offset + 64U, event.dpi);
    write_u32(response_span, payload_offset + 68U,
        static_cast<std::uint32_t>(event.native_error_code));
    write_u32(response_span, payload_offset + 72U,
        static_cast<std::uint32_t>(event.error_code.size()));
    std::ranges::transform(event.error_code,
        response.begin() + payload_offset + lifecycle_fixed_payload_bytes,
        [](const char value) { return static_cast<std::byte>(value); });
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

bool write_cloud_ocr_crop_event(
    HANDLE pipe,
    const BootstrapConfig& config,
    const CloudOcrCropEvent& event,
    std::mutex& write_gate) noexcept
{
    constexpr std::uint32_t cloud_ocr_crop_request_kind = 7U;
    auto payload = encode_cloud_ocr_crop_request(event);
    if (!payload.has_value()) return false;
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload->size());
    const auto bytes = std::span<std::byte>(response);
    write_u32(bytes, 0U, static_cast<std::uint32_t>(wire_header_bytes + payload->size()));
    write_u32(bytes, 4U, wire_magic);
    write_u32(bytes, 8U, protocol_version);
    write_u32(bytes, 12U, cloud_ocr_crop_request_kind);
    GUID event_id{};
    if (FAILED(CoCreateGuid(&event_id)))
    {
        SecureZeroMemory(payload->data(), payload->size());
        return false;
    }
    std::ranges::copy(std::as_bytes(std::span{&event_id, 1U}), response.begin() + 16U);
    std::ranges::copy(config.runtime_epoch, response.begin() + 32U);
    write_u64(bytes, 48U, event.deadline_utc_ticks);
    write_u32(bytes, 56U, static_cast<std::uint32_t>(payload->size()));
    std::ranges::copy(*payload, response.begin() + sizeof(std::uint32_t) + wire_header_bytes);
    SecureZeroMemory(payload->data(), payload->size());
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

bool write_runtime_budget_event(
    HANDLE pipe,
    const BootstrapConfig& config,
    const RuntimeCaptureController& controller,
    const std::uint64_t revision,
    const std::uint64_t captured_at_ticks,
    std::mutex& write_gate) noexcept
{
    constexpr std::uint32_t runtime_budget_snapshot_kind = 25U;
    constexpr std::uint32_t budget_schema_version = 1U;
    constexpr std::uint32_t bytes_unit = 0U;
    constexpr std::uint32_t slots_unit = 1U;
    constexpr std::size_t budget_header_bytes = 40U;
    constexpr std::size_t budget_pool_fixed_bytes = 32U;
    constexpr std::uint64_t engine_committed_limit = 2'147'483'648ULL;
    constexpr std::uint64_t target_limit = 8U;
    constexpr std::uint64_t capture_source_limit = 8U;
    struct Pool final
    {
        std::string name;
        std::uint32_t unit{};
        std::uint64_t limit{};
        std::uint64_t committed{};
    };

    PROCESS_MEMORY_COUNTERS_EX counters{};
    counters.cb = sizeof(counters);
    if (GetProcessMemoryInfo(
            GetCurrentProcess(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters),
            sizeof(counters)) == FALSE)
        return false;
    const std::uint64_t private_bytes = counters.PrivateUsage;
    if (private_bytes > engine_committed_limit ||
        controller.target_count() > target_limit ||
        controller.capture_source_count() > capture_source_limit)
        return false;
    std::vector<AdapterGpuBudget> gpu_budgets;
    if (!controller.try_get_gpu_budgets(gpu_budgets)) return false;
    std::vector<Pool> pools;
    pools.reserve(3U + gpu_budgets.size());
    pools.push_back(Pool{"engine.committed.bytes", bytes_unit,
        engine_committed_limit, private_bytes});
    pools.push_back(Pool{"engine.targets.slots", slots_unit,
        target_limit, controller.target_count()});
    pools.push_back(Pool{"engine.captureSources.slots", slots_unit,
        capture_source_limit, controller.capture_source_count()});
    for (const AdapterGpuBudget& gpu : gpu_budgets)
    {
        std::array<char, 16U> encoded_key{};
        const auto conversion = std::to_chars(
            encoded_key.data(), encoded_key.data() + encoded_key.size(),
            gpu.adapter_key, 16);
        if (conversion.ec != std::errc{}) return false;
        std::string name{"engine.gpu."};
        name.append(encoded_key.data(), conversion.ptr);
        name.append(".local.bytes");
        pools.push_back(Pool{std::move(name), bytes_unit, gpu.limit, gpu.committed});
    }
    std::size_t payload_bytes = budget_header_bytes;
    for (const Pool& pool : pools)
        payload_bytes += budget_pool_fixed_bytes + pool.name.size();
    std::vector<std::byte> response(
        sizeof(std::uint32_t) + wire_header_bytes + payload_bytes);
    const auto bytes = std::span<std::byte>(response);
    write_u32(bytes, 0U, static_cast<std::uint32_t>(wire_header_bytes + payload_bytes));
    write_u32(bytes, 4U, wire_magic);
    write_u32(bytes, 8U, protocol_version);
    write_u32(bytes, 12U, runtime_budget_snapshot_kind);
    GUID event_id{};
    if (FAILED(CoCreateGuid(&event_id))) return false;
    std::ranges::copy(std::as_bytes(std::span{&event_id, 1U}), response.begin() + 16U);
    std::ranges::copy(config.runtime_epoch, response.begin() + 32U);
    write_u64(bytes, 48U, captured_at_ticks + response_lifetime_ticks);
    write_u32(bytes, 56U, static_cast<std::uint32_t>(payload_bytes));

    constexpr std::size_t payload_offset = sizeof(std::uint32_t) + wire_header_bytes;
    write_u32(bytes, payload_offset, budget_schema_version);
    write_u32(bytes, payload_offset + 4U, static_cast<std::uint32_t>(pools.size()));
    write_u64(bytes, payload_offset + 8U, revision);
    write_u64(bytes, payload_offset + 16U, captured_at_ticks);
    std::ranges::copy(config.runtime_epoch, response.begin() + payload_offset + 24U);
    std::size_t offset = payload_offset + budget_header_bytes;
    for (const Pool& pool : pools)
    {
        write_u32(bytes, offset, static_cast<std::uint32_t>(pool.name.size()));
        write_u32(bytes, offset + 4U, pool.unit);
        write_u64(bytes, offset + 8U, pool.limit);
        write_u64(bytes, offset + 16U, pool.committed);
        write_u64(bytes, offset + 24U, 0U);
        std::ranges::transform(
            pool.name,
            response.begin() + offset + budget_pool_fixed_bytes,
            [](const char value) { return static_cast<std::byte>(value); });
        offset += budget_pool_fixed_bytes + pool.name.size();
    }
    std::scoped_lock lock(write_gate);
    const bool written = write_exact(pipe, response) && FlushFileBuffers(pipe) != FALSE;
    SecureZeroMemory(response.data(), response.size());
    return written;
}

ServerExitCode process_messages(HANDLE pipe, const BootstrapConfig& config) noexcept
{
    std::mutex write_gate;
    RuntimePolicyState policy_state;
    RuntimeCaptureController capture_controller(
        [pipe, &config, &write_gate](const CaptureTargetLifecycleEvent& event) noexcept
        {
            static_cast<void>(write_target_lifecycle_event(
                pipe, config, event, write_gate));
        },
        config.runtime_epoch,
        [pipe, &config, &write_gate](const CloudOcrCropEvent& event) noexcept
        {
            return write_cloud_ocr_crop_event(pipe, config, event, write_gate);
        },
        [pipe, &config, &write_gate](const OcrResultCommand& result) noexcept
        {
            return write_native_ocr_result_event(
                pipe, config, result, write_gate);
        });
    std::uint64_t budget_revision{1U};
    std::uint64_t last_budget_ticks = utc_ticks();
    if (!write_runtime_budget_event(
            pipe,
            config,
            capture_controller,
            budget_revision++,
            last_budget_ticks,
            write_gate))
        return ServerExitCode::protocol_failed;
    while (true)
    {
        std::array<std::byte, sizeof(std::uint32_t)> prefix{};
        if (!read_exact(pipe, prefix))
        {
            return ServerExitCode::success;
        }
        const std::uint32_t body_length = read_u32(prefix, 0U);
        if (body_length < wire_header_bytes || body_length > maximum_message_bytes)
        {
            return ServerExitCode::protocol_failed;
        }

        std::vector<std::byte> body(body_length);
        if (!read_exact(pipe, body))
        {
            SecureZeroMemory(body.data(), body.size());
            return ServerExitCode::protocol_failed;
        }
        const auto body_span = std::span<const std::byte>(body);
        const std::uint32_t payload_length = read_u32(body_span, 52U);
        const std::uint32_t request_kind = read_u32(body_span, 8U);
        const bool valid_header = read_u32(body_span, 0U) == wire_magic &&
            read_u32(body_span, 4U) == protocol_version &&
            std::ranges::any_of(body_span.subspan(12U, 16U), [](const std::byte value)
            {
                return value != std::byte{};
            }) &&
            std::ranges::equal(body_span.subspan(28U, 16U), config.runtime_epoch) &&
            read_u64(body_span, 44U) > utc_ticks() &&
            payload_length == body_length - wire_header_bytes;
        const bool empty_control = (request_kind == control_request_kind ||
            request_kind == shutdown_request_kind) && payload_length == 0U;
        const auto capture_command = request_kind == capture_target_command_kind &&
            valid_header
            ? parse_capture_target_command(body_span.subspan(wire_header_bytes))
            : std::nullopt;
        auto overlay_command = request_kind == overlay_desired_state_kind && valid_header
            ? parse_overlay_desired_state(body_span.subspan(wire_header_bytes))
            : std::nullopt;
        const auto policy_command = request_kind == policy_revision_kind && valid_header
            ? parse_policy_revision(body_span.subspan(wire_header_bytes))
            : std::nullopt;
        auto processing_command = request_kind == processing_configuration_kind && valid_header
            ? parse_processing_configuration(body_span.subspan(wire_header_bytes))
            : std::nullopt;
        const auto ocr_result = request_kind == ocr_result_kind && valid_header
            ? parse_ocr_result(body_span.subspan(wire_header_bytes))
            : std::nullopt;
        const bool overlay_epoch_valid = !overlay_command.has_value() ||
            std::ranges::equal(overlay_command->runtime_epoch, config.runtime_epoch);
        const bool ocr_epoch_valid = !ocr_result.has_value() ||
            std::ranges::equal(ocr_result->token.runtime_epoch, config.runtime_epoch);
        const bool supported = empty_control || capture_command.has_value() ||
            overlay_command.has_value() || policy_command.has_value() ||
            processing_command.has_value() || ocr_result.has_value();
        if (!valid_header || !supported || !overlay_epoch_valid || !ocr_epoch_valid)
        {
            SecureZeroMemory(body.data(), body.size());
            return ServerExitCode::protocol_failed;
        }

        bool sent{};
        if (capture_command.has_value())
        {
            const CaptureTargetApplyResult result = capture_controller.apply(*capture_command);
            sent = write_capture_target_response(
                pipe, body_span, *capture_command, result, write_gate);
        }
        else if (overlay_command.has_value())
        {
            auto state = std::make_shared<overlay::desired_state>(
                std::move(*overlay_command));
            const OverlayApplyResult result = capture_controller.apply_overlay(state);
            sent = write_overlay_response(
                pipe, body_span, *state, result, write_gate);
        }
        else if (policy_command.has_value())
        {
            PolicyRevisionCommand command = *policy_command;
            RuntimePolicyState next_policy = policy_state;
            std::string rejection = next_policy.apply(command);
            if (rejection.empty()) rejection = capture_controller.apply_policy(command);
            if (rejection.empty()) policy_state = std::move(next_policy);
            sent = write_policy_response(
                pipe, body_span, *policy_command, rejection, write_gate);
        }
        else if (processing_command.has_value())
        {
            const ProcessingConfigurationCommand response_identity = *processing_command;
            const ProcessingApplyResult result = capture_controller.apply_processing(
                std::move(*processing_command));
            sent = write_processing_response(
                pipe, body_span, response_identity, result, write_gate);
        }
        else if (ocr_result.has_value())
        {
            const OcrResultApplyResult result = capture_controller.apply_ocr_result(
                *ocr_result);
            sent = write_ocr_result_response(
                pipe, body_span, *ocr_result, result, write_gate);
            if (sent && result.accepted)
                sent = write_ocr_result_event(
                    pipe, config, body_span.subspan(wire_header_bytes), write_gate);
        }
        else
        {
            const std::uint32_t response_kind = request_kind == control_request_kind
                ? control_response_kind
                : shutdown_acknowledgement_kind;
            sent = write_empty_response(pipe, body_span, response_kind, write_gate);
        }
        SecureZeroMemory(body.data(), body.size());
        if (!sent)
        {
            return ServerExitCode::protocol_failed;
        }
        if (request_kind == shutdown_request_kind)
        {
            return ServerExitCode::success;
        }
        const std::uint64_t budget_now = utc_ticks();
        constexpr std::uint64_t budget_publish_interval_ticks = 10'000'000ULL;
        if ((capture_command.has_value() ||
                budget_now - last_budget_ticks >= budget_publish_interval_ticks) &&
            !write_runtime_budget_event(
                pipe,
                config,
                capture_controller,
                budget_revision++,
                budget_now,
                write_gate))
            return ServerExitCode::protocol_failed;
        if (capture_command.has_value() ||
            budget_now - last_budget_ticks >= budget_publish_interval_ticks)
            last_budget_ticks = budget_now;
    }
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

    const ServerExitCode message_result = process_messages(pipe.get(), config);
    DisconnectNamedPipe(pipe.get());
    return message_result;
}
}
