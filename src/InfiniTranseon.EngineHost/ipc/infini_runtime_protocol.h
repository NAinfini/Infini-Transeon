#pragma once

#include <infini/overlay/overlay_types.hpp>

#include <array>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <span>
#include <string>
#include <vector>

namespace infini::runtime
{
inline constexpr std::uint32_t protocol_version = 1U;
inline constexpr std::uint32_t bootstrap_magic = 0x42525449U;
inline constexpr std::uint32_t wire_magic = 0x50525449U;
inline constexpr std::uint32_t handshake_request_kind = 0U;
inline constexpr std::uint32_t handshake_response_kind = 1U;
inline constexpr std::uint32_t capture_target_command_kind = 19U;
inline constexpr std::uint32_t capture_target_acknowledgement_kind = 20U;
inline constexpr std::uint32_t overlay_desired_state_kind = 10U;
inline constexpr std::uint32_t policy_revision_kind = 11U;
inline constexpr std::uint32_t policy_acknowledgement_kind = 12U;
inline constexpr std::uint32_t overlay_acknowledgement_kind = 21U;
inline constexpr std::uint32_t processing_configuration_kind = 22U;
inline constexpr std::uint32_t processing_configuration_acknowledgement_kind = 23U;
inline constexpr std::uint32_t ocr_result_kind = 6U;
inline constexpr std::uint32_t ocr_result_acknowledgement_kind = 24U;
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

enum class CaptureTargetOperation : std::uint8_t
{
    upsert = 1U,
    remove = 2U,
};

enum class CaptureTargetKind : std::uint8_t
{
    window = 1U,
    monitor = 2U,
    desktop_region = 3U,
};

struct CaptureTargetCommand final
{
    CaptureTargetOperation operation{};
    CaptureTargetKind kind{};
    std::uint64_t command_revision{};
    std::array<std::byte, 16U> target_id{};
    std::array<std::byte, 16U> target_instance_id{};
    std::uint64_t native_handle{};
    std::int32_t region_x{};
    std::int32_t region_y{};
    std::int32_t region_width{};
    std::int32_t region_height{};
};

struct PolicyRegionState final
{
    std::array<std::byte, 16U> region_id{};
    std::string policy;
};

struct PolicyRevisionCommand final
{
    std::uint64_t revision{};
    std::array<std::byte, 16U> profile_id{};
    std::uint64_t profile_revision{};
    std::vector<PolicyRegionState> regions;
};

struct ProcessingRegion final
{
    std::array<std::byte, 16U> region_id{};
    double x{};
    double y{};
    double width{};
    double height{};
    std::uint8_t priority{};
    std::uint8_t area_mode{};
    bool lock_degradation{};
    bool detect_orientation{};
    bool use_cloud_ocr{};
    std::uint64_t cloud_consent_policy_revision{};
    std::uint32_t recognition_interval_milliseconds{};
    std::uint8_t line_break_mode{};
    double detection_scale{};
    std::string ocr_provider_id;
    std::string recognition_language;
    std::string preprocessing_pipeline;
};

struct ProcessingConfigurationCommand final
{
    std::uint64_t configuration_revision{};
    std::array<std::byte, 16U> profile_id{};
    std::uint64_t profile_revision{};
    std::array<std::byte, 16U> target_instance_id{};
    std::uint32_t detection_long_edge{};
    std::uint32_t remaining_area_interval_milliseconds{};
    bool scan_remaining_area{};
    std::vector<ProcessingRegion> regions;
};

struct OcrExecutionIdentity final
{
    std::array<std::byte, 16U> runtime_epoch{};
    std::array<std::byte, 16U> target_instance_id{};
    std::uint8_t area_kind{};
    std::array<std::byte, 16U> region_id{};
    std::array<std::byte, 16U> text_track_id{};
    std::uint64_t source_generation{};
    std::uint64_t profile_revision{};
    std::array<std::byte, 16U> ocr_run_id{};
    std::uint32_t attempt{};
    std::uint64_t result_sequence{};
};

struct OcrResultLine final
{
    std::u16string text;
    double x{};
    double y{};
    double width{};
    double height{};
    double confidence{};
    std::int32_t orientation_degrees{};
    bool vertical{};
};

struct OcrResultCommand final
{
    OcrExecutionIdentity token;
    bool stable{};
    std::u16string model_id;
    std::u16string model_version;
    std::string terminal_error_code;
    std::vector<OcrResultLine> lines;
};

struct CloudOcrCropEvent final
{
    OcrExecutionIdentity token;
    std::string mime_type;
    std::string provider_id;
    std::vector<std::byte> encoded_crop;
    std::uint32_t pixel_width{};
    std::uint32_t pixel_height{};
    std::uint64_t consent_policy_revision{};
    std::uint64_t deadline_utc_ticks{};
    std::uint32_t encoded_byte_ceiling{};
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

[[nodiscard]] std::optional<CaptureTargetCommand> parse_capture_target_command(
    std::span<const std::byte> bytes) noexcept;

[[nodiscard]] std::optional<overlay::desired_state> parse_overlay_desired_state(
    std::span<const std::byte> bytes) noexcept;

[[nodiscard]] std::optional<PolicyRevisionCommand> parse_policy_revision(
    std::span<const std::byte> bytes) noexcept;

[[nodiscard]] std::optional<ProcessingConfigurationCommand>
parse_processing_configuration(std::span<const std::byte> bytes) noexcept;

[[nodiscard]] std::optional<OcrResultCommand>
    parse_ocr_result(std::span<const std::byte> bytes) noexcept;

[[nodiscard]] std::optional<std::vector<std::byte>>
    encode_cloud_ocr_crop_request(const CloudOcrCropEvent& event) noexcept;

[[nodiscard]] std::optional<std::vector<std::byte>>
    encode_ocr_result(const OcrResultCommand& result) noexcept;

[[nodiscard]] bool authenticate_handshake(
    std::span<const std::byte> body,
    std::uint32_t actual_client_process_id,
    std::uint32_t local_process_id,
    BootstrapConfig& config,
    std::uint64_t utc_now_ticks) noexcept;
}
