#include <algorithm>
#include <array>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <span>
#include <string>
#include <vector>

#include <infini_runtime_protocol.h>

namespace
{
using infini::runtime::BootstrapConfig;
using infini::runtime::ProtocolError;

void require(const bool condition)
{
    if (!condition)
    {
        std::abort();
    }
}

template <typename T>
void append_little_endian(std::vector<std::byte>& bytes, T value)
{
    for (std::size_t index = 0; index < sizeof(T); ++index)
    {
        bytes.push_back(static_cast<std::byte>(value & 0xffU));
        value = static_cast<T>(value >> 8U);
    }
}

std::vector<std::byte> valid_bootstrap()
{
    constexpr std::string_view pipe_name =
        "infini-transeon.0123456789abcdef0123456789abcdef";
    std::vector<std::byte> bytes;
    append_little_endian(bytes, infini::runtime::bootstrap_magic);
    append_little_endian(bytes, infini::runtime::protocol_version);
    append_little_endian(bytes, std::uint32_t{4242});
    for (std::uint8_t value = 1; value <= 16; ++value)
    {
        bytes.push_back(static_cast<std::byte>(value));
    }
    for (std::uint8_t value = 17; value <= 48; ++value)
    {
        bytes.push_back(static_cast<std::byte>(value));
    }
    append_little_endian(bytes, static_cast<std::uint32_t>(pipe_name.size()));
    for (const char value : pipe_name)
    {
        bytes.push_back(static_cast<std::byte>(value));
    }
    return bytes;
}

void write_u32(std::span<std::byte> bytes, const std::size_t offset,
    const std::uint32_t value)
{
    for (std::size_t index = 0; index < sizeof(value); ++index)
    {
        bytes[offset + index] = static_cast<std::byte>((value >> (index * 8U)) & 0xffU);
    }
}

void write_u64(std::span<std::byte> bytes, const std::size_t offset,
    const std::uint64_t value)
{
    for (std::size_t index = 0; index < sizeof(value); ++index)
    {
        bytes[offset + index] = static_cast<std::byte>((value >> (index * 8U)) & 0xffU);
    }
}

BootstrapConfig handshake_config()
{
    BootstrapConfig config;
    config.expected_client_process_id = 4242U;
    for (std::size_t index = 0; index < config.runtime_epoch.size(); ++index)
    {
        config.runtime_epoch[index] = static_cast<std::byte>(index + 1U);
    }
    for (std::size_t index = 0; index < config.nonce.size(); ++index)
    {
        config.nonce[index] = static_cast<std::byte>(index + 17U);
    }
    return config;
}

std::array<std::byte, 96U> valid_handshake(const BootstrapConfig& config)
{
    std::array<std::byte, 96U> body{};
    write_u32(body, 0U, infini::runtime::wire_magic);
    write_u32(body, 4U, infini::runtime::protocol_version);
    write_u32(body, 8U, infini::runtime::handshake_request_kind);
    body[12U] = std::byte{1};
    std::ranges::copy(config.runtime_epoch, body.begin() + 28U);
    write_u64(body, 44U, 1'000U);
    write_u32(body, 52U, 40U);
    write_u32(body, 56U, 4242U);
    write_u32(body, 60U, 9001U);
    std::ranges::copy(config.nonce, body.begin() + 64U);
    return body;
}
}

int main()
{
    std::vector<std::byte> bytes = valid_bootstrap();
    const auto parsed = infini::runtime::parse_bootstrap(bytes);
    require(parsed.has_value());
    require(parsed->expected_client_process_id == 4242U);
    require(parsed->pipe_name ==
        "infini-transeon.0123456789abcdef0123456789abcdef");
    require(parsed->runtime_epoch.front() == std::byte{1});
    require(parsed->runtime_epoch.back() == std::byte{16});
    require(parsed->nonce.front() == std::byte{17});
    require(parsed->nonce.back() == std::byte{48});

    bytes[0] = std::byte{0};
    const auto bad_magic = infini::runtime::parse_bootstrap(bytes);
    require(!bad_magic.has_value());
    require(bad_magic.error() == ProtocolError::invalid_bootstrap);

    bytes = valid_bootstrap();
    bytes.pop_back();
    const auto truncated = infini::runtime::parse_bootstrap(bytes);
    require(!truncated.has_value());
    require(truncated.error() == ProtocolError::invalid_bootstrap);

    bytes = valid_bootstrap();
    constexpr std::size_t pipe_name_offset = 4U + 4U + 4U + 16U + 32U + 4U;
    bytes[pipe_name_offset] = std::byte{'X'};
    const auto invalid_name = infini::runtime::parse_bootstrap(bytes);
    require(!invalid_name.has_value());
    require(invalid_name.error() == ProtocolError::invalid_pipe_name);

    BootstrapConfig accepted_config = handshake_config();
    auto accepted_body = valid_handshake(accepted_config);
    require(infini::runtime::authenticate_handshake(
        accepted_body, 4242U, 9001U, accepted_config, 500U));
    require(std::ranges::all_of(accepted_config.nonce,
        [](const std::byte value) { return value == std::byte{}; }));

    BootstrapConfig wrong_nonce_config = handshake_config();
    auto wrong_nonce_body = valid_handshake(wrong_nonce_config);
    wrong_nonce_body.back() ^= std::byte{1};
    require(!infini::runtime::authenticate_handshake(
        wrong_nonce_body, 4242U, 9001U, wrong_nonce_config, 500U));
    require(std::ranges::all_of(wrong_nonce_config.nonce,
        [](const std::byte value) { return value == std::byte{}; }));

    BootstrapConfig wrong_pid_config = handshake_config();
    auto wrong_pid_body = valid_handshake(wrong_pid_config);
    require(!infini::runtime::authenticate_handshake(
        wrong_pid_body, 7777U, 9001U, wrong_pid_config, 500U));
    require(std::ranges::all_of(wrong_pid_config.nonce,
        [](const std::byte value) { return value == std::byte{}; }));

    return EXIT_SUCCESS;
}
