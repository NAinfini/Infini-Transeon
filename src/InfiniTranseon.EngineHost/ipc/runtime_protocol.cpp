#include "infini_runtime_protocol.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <utility>

namespace infini::runtime
{
namespace
{
constexpr std::size_t fixed_bootstrap_bytes = 4U + 4U + 4U + 16U + nonce_bytes + 4U;
constexpr std::string_view pipe_prefix = "infini-transeon.";
constexpr std::size_t pipe_name_bytes = pipe_prefix.size() + 32U;

std::uint32_t read_u32(const std::span<const std::byte> bytes, const std::size_t offset) noexcept
{
    std::uint32_t result{};
    for (std::size_t index = 0; index < sizeof(result); ++index)
    {
        result |= std::to_integer<std::uint32_t>(bytes[offset + index]) << (index * 8U);
    }
    return result;
}

std::uint64_t read_u64(const std::span<const std::byte> bytes, const std::size_t offset) noexcept
{
    std::uint64_t result{};
    for (std::size_t index = 0; index < sizeof(result); ++index)
    {
        result |= std::to_integer<std::uint64_t>(bytes[offset + index]) << (index * 8U);
    }
    return result;
}

bool valid_pipe_name(const std::string_view value) noexcept
{
    if (value.size() != pipe_name_bytes || !value.starts_with(pipe_prefix))
    {
        return false;
    }
    return std::ranges::all_of(value.substr(pipe_prefix.size()), [](const char character)
    {
        return (character >= '0' && character <= '9') ||
            (character >= 'a' && character <= 'f');
    });
}

void clear_nonce(std::array<std::byte, nonce_bytes>& nonce) noexcept
{
    SecureZeroMemory(nonce.data(), nonce.size());
}

bool nonzero(const std::span<const std::byte> bytes) noexcept
{
    return std::ranges::any_of(bytes, [](const std::byte value)
    {
        return value != std::byte{};
    });
}

bool constant_time_equal(const std::span<const std::byte> left,
    const std::span<const std::byte> right) noexcept
{
    if (left.size() != right.size())
    {
        return false;
    }
    unsigned int difference{};
    for (std::size_t index = 0; index < left.size(); ++index)
    {
        difference |= std::to_integer<unsigned int>(left[index] ^ right[index]);
    }
    return difference == 0U;
}
}

BootstrapConfig::~BootstrapConfig()
{
    clear_nonce(nonce);
}

BootstrapConfig::BootstrapConfig(BootstrapConfig&& other) noexcept
    : expected_client_process_id(other.expected_client_process_id),
      runtime_epoch(other.runtime_epoch),
      nonce(other.nonce),
      pipe_name(std::move(other.pipe_name))
{
    clear_nonce(other.nonce);
}

BootstrapConfig& BootstrapConfig::operator=(BootstrapConfig&& other) noexcept
{
    if (this != &other)
    {
        clear_nonce(nonce);
        expected_client_process_id = other.expected_client_process_id;
        runtime_epoch = other.runtime_epoch;
        nonce = other.nonce;
        pipe_name = std::move(other.pipe_name);
        clear_nonce(other.nonce);
    }
    return *this;
}

BootstrapParseResult::BootstrapParseResult(BootstrapConfig value) noexcept
    : value_(std::move(value)), error_(ProtocolError::none)
{
}

BootstrapParseResult::BootstrapParseResult(const ProtocolError error) noexcept
    : error_(error)
{
}

bool BootstrapParseResult::has_value() const noexcept
{
    return value_.has_value();
}

ProtocolError BootstrapParseResult::error() const noexcept
{
    return error_;
}

const BootstrapConfig* BootstrapParseResult::operator->() const noexcept
{
    return &value_.value();
}

BootstrapConfig BootstrapParseResult::take_value()
{
    return std::move(value_.value());
}

BootstrapParseResult parse_bootstrap(const std::span<const std::byte> bytes)
{
    if (bytes.size() < fixed_bootstrap_bytes ||
        read_u32(bytes, 0U) != bootstrap_magic ||
        read_u32(bytes, 4U) != protocol_version)
    {
        return BootstrapParseResult(ProtocolError::invalid_bootstrap);
    }

    const std::uint32_t expected_client_process_id = read_u32(bytes, 8U);
    const std::uint32_t name_length = read_u32(bytes, fixed_bootstrap_bytes - 4U);
    if (expected_client_process_id == 0U || name_length != pipe_name_bytes ||
        bytes.size() != fixed_bootstrap_bytes + name_length)
    {
        return BootstrapParseResult(ProtocolError::invalid_bootstrap);
    }

    BootstrapConfig config;
    config.expected_client_process_id = expected_client_process_id;
    std::ranges::copy(bytes.subspan(12U, config.runtime_epoch.size()), config.runtime_epoch.begin());
    if (std::ranges::all_of(config.runtime_epoch, [](const std::byte value)
        { return value == std::byte{}; }))
    {
        return BootstrapParseResult(ProtocolError::invalid_bootstrap);
    }
    std::ranges::copy(bytes.subspan(28U, config.nonce.size()), config.nonce.begin());
    config.pipe_name.reserve(name_length);
    for (const std::byte value : bytes.subspan(fixed_bootstrap_bytes, name_length))
    {
        config.pipe_name.push_back(static_cast<char>(value));
    }
    if (!valid_pipe_name(config.pipe_name))
    {
        return BootstrapParseResult(ProtocolError::invalid_pipe_name);
    }
    return BootstrapParseResult(std::move(config));
}


bool authenticate_handshake(
    const std::span<const std::byte> body,
    const std::uint32_t actual_client_process_id,
    const std::uint32_t local_process_id,
    BootstrapConfig& config,
    const std::uint64_t utc_now_ticks) noexcept
{
    constexpr std::size_t wire_header_bytes = 56U;
    constexpr std::size_t request_payload_bytes = 40U;
    const bool valid = body.size() == wire_header_bytes + request_payload_bytes &&
        read_u32(body, 0U) == wire_magic &&
        read_u32(body, 4U) == protocol_version &&
        read_u32(body, 8U) == handshake_request_kind &&
        nonzero(body.subspan(12U, 16U)) &&
        std::ranges::equal(body.subspan(28U, 16U), config.runtime_epoch) &&
        read_u64(body, 44U) > utc_now_ticks &&
        read_u32(body, 52U) == request_payload_bytes &&
        actual_client_process_id == config.expected_client_process_id &&
        read_u32(body, 56U) == actual_client_process_id &&
        read_u32(body, 60U) == local_process_id &&
        constant_time_equal(body.subspan(64U, nonce_bytes), config.nonce);
    clear_nonce(config.nonce);
    return valid;
}
}
