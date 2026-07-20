#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string>

namespace infini::runtime
{
inline constexpr std::uint32_t protocol_version = 1U;
inline constexpr std::uint32_t bootstrap_magic = 0x42525449U;
inline constexpr std::uint32_t wire_magic = 0x50525449U;
inline constexpr std::uint32_t handshake_request_kind = 0U;
inline constexpr std::uint32_t handshake_response_kind = 1U;
inline constexpr std::size_t nonce_bytes = 32U;

enum class ProtocolError
{
    none,
    invalid_bootstrap,
    invalid_pipe_name,
};

struct BootstrapConfig final
{
    BootstrapConfig() = default;
    ~BootstrapConfig();
    BootstrapConfig(const BootstrapConfig&) = delete;
    BootstrapConfig& operator=(const BootstrapConfig&) = delete;
    BootstrapConfig(BootstrapConfig&& other) noexcept;
    BootstrapConfig& operator=(BootstrapConfig&& other) noexcept;

    std::uint32_t expected_client_process_id{};
    std::array<std::byte, 16U> runtime_epoch{};
    std::array<std::byte, nonce_bytes> nonce{};
    std::string pipe_name;
};

class BootstrapParseResult final
{
public:
    explicit BootstrapParseResult(BootstrapConfig value) noexcept;
    explicit BootstrapParseResult(ProtocolError error) noexcept;

    [[nodiscard]] bool has_value() const noexcept;
    [[nodiscard]] ProtocolError error() const noexcept;
    [[nodiscard]] const BootstrapConfig* operator->() const noexcept;
    [[nodiscard]] BootstrapConfig take_value();

private:
    std::optional<BootstrapConfig> value_;
    ProtocolError error_;
};

[[nodiscard]] BootstrapParseResult parse_bootstrap(std::span<const std::byte> bytes);

[[nodiscard]] bool authenticate_handshake(
    std::span<const std::byte> body,
    std::uint32_t actual_client_process_id,
    std::uint32_t local_process_id,
    BootstrapConfig& config,
    std::uint64_t utc_now_ticks) noexcept;
}
