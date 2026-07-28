#include "infini_runtime_protocol.h"

#define WIN32_LEAN_AND_MEAN
#include <Windows.h>

#include <algorithm>
#include <bit>
#include <cmath>
#include <limits>
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

std::uint16_t read_u16(const std::span<const std::byte> bytes,
    const std::size_t offset) noexcept
{
    return static_cast<std::uint16_t>(
        std::to_integer<std::uint16_t>(bytes[offset]) |
        (std::to_integer<std::uint16_t>(bytes[offset + 1U]) << 8U));
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

std::int32_t read_i32(const std::span<const std::byte> bytes,
    const std::size_t offset) noexcept
{
    return static_cast<std::int32_t>(read_u32(bytes, offset));
}

double read_f64(const std::span<const std::byte> bytes,
    const std::size_t offset) noexcept
{
    return std::bit_cast<double>(read_u64(bytes, offset));
}

void write_u16(const std::span<std::byte> bytes, const std::size_t offset,
    const std::uint16_t value) noexcept
{
    bytes[offset] = static_cast<std::byte>(value & 0xffU);
    bytes[offset + 1U] = static_cast<std::byte>((value >> 8U) & 0xffU);
}

void write_u32(const std::span<std::byte> bytes, const std::size_t offset,
    const std::uint32_t value) noexcept
{
    for (std::size_t index{}; index < sizeof(value); ++index)
        bytes[offset + index] = static_cast<std::byte>((value >> (index * 8U)) & 0xffU);
}

void write_u64(const std::span<std::byte> bytes, const std::size_t offset,
    const std::uint64_t value) noexcept
{
    for (std::size_t index{}; index < sizeof(value); ++index)
        bytes[offset + index] = static_cast<std::byte>((value >> (index * 8U)) & 0xffULL);
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

bool reserved_zero(const std::span<const std::byte> bytes) noexcept
{
    return std::ranges::all_of(bytes, [](const std::byte value)
    {
        return value == std::byte{};
    });
}

std::optional<std::u16string> utf8_to_utf16(
    const std::span<const std::byte> bytes) noexcept
{
    if (bytes.empty()) return std::u16string{};
    if (bytes.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)()))
        return std::nullopt;
    const auto* text = reinterpret_cast<const char*>(bytes.data());
    const int length = static_cast<int>(bytes.size());
    const int characters = MultiByteToWideChar(
        CP_UTF8, MB_ERR_INVALID_CHARS, text, length, nullptr, 0);
    if (characters <= 0) return std::nullopt;
    std::wstring converted(static_cast<std::size_t>(characters), L'\0');
    if (MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, text, length,
        converted.data(), characters) != characters)
        return std::nullopt;
    return std::u16string(
        reinterpret_cast<const char16_t*>(converted.data()), converted.size());
}

std::optional<std::string> utf16_to_utf8(const std::u16string_view value) noexcept
{
    if (value.empty()) return std::string{};
    if (value.size() > static_cast<std::size_t>((std::numeric_limits<int>::max)()))
        return std::nullopt;
    const auto* text = reinterpret_cast<const wchar_t*>(value.data());
    const int length = static_cast<int>(value.size());
    const int bytes = WideCharToMultiByte(
        CP_UTF8, WC_ERR_INVALID_CHARS, text, length, nullptr, 0, nullptr, nullptr);
    if (bytes <= 0) return std::nullopt;
    std::string converted(static_cast<std::size_t>(bytes), '\0');
    if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text, length,
        converted.data(), bytes, nullptr, nullptr) != bytes)
        return std::nullopt;
    return converted;
}

overlay::color_rgba read_color(
    const std::span<const std::byte> bytes,
    const std::size_t offset) noexcept
{
    constexpr float scale = 1.0F / 255.0F;
    return {
        std::to_integer<std::uint8_t>(bytes[offset]) * scale,
        std::to_integer<std::uint8_t>(bytes[offset + 1U]) * scale,
        std::to_integer<std::uint8_t>(bytes[offset + 2U]) * scale,
        std::to_integer<std::uint8_t>(bytes[offset + 3U]) * scale,
    };
}

template <typename Identity>
bool unique_identity(
    std::vector<Identity>& identities,
    const Identity& candidate)
{
    if (!nonzero(candidate) ||
        std::ranges::find(identities, candidate) != identities.end())
        return false;
    identities.push_back(candidate);
    return true;
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

std::optional<CaptureTargetCommand> parse_capture_target_command(
    const std::span<const std::byte> bytes) noexcept
{
    constexpr std::size_t payload_bytes = 72U;
    constexpr std::uint32_t schema_version = 1U;
    if (bytes.size() != payload_bytes || read_u32(bytes, 0U) != schema_version ||
        bytes[6U] != std::byte{} || bytes[7U] != std::byte{})
    {
        return std::nullopt;
    }

    CaptureTargetCommand command;
    command.operation = static_cast<CaptureTargetOperation>(
        std::to_integer<std::uint8_t>(bytes[4U]));
    command.kind = static_cast<CaptureTargetKind>(
        std::to_integer<std::uint8_t>(bytes[5U]));
    command.command_revision = read_u64(bytes, 8U);
    std::ranges::copy(bytes.subspan(16U, 16U), command.target_id.begin());
    std::ranges::copy(bytes.subspan(32U, 16U), command.target_instance_id.begin());
    command.native_handle = read_u64(bytes, 48U);
    command.region_x = read_i32(bytes, 56U);
    command.region_y = read_i32(bytes, 60U);
    command.region_width = read_i32(bytes, 64U);
    command.region_height = read_i32(bytes, 68U);

    const bool known_operation = command.operation == CaptureTargetOperation::upsert ||
        command.operation == CaptureTargetOperation::remove;
    const bool known_kind = command.kind == CaptureTargetKind::window ||
        command.kind == CaptureTargetKind::monitor ||
        command.kind == CaptureTargetKind::desktop_region;
    const bool identities_valid = command.command_revision > 0U &&
        command.command_revision <= static_cast<std::uint64_t>(INT64_MAX) &&
        nonzero(command.target_id) && nonzero(command.target_instance_id);
    if (!known_operation || !known_kind || !identities_valid)
    {
        return std::nullopt;
    }

    const bool empty_region = command.region_x == 0 && command.region_y == 0 &&
        command.region_width == 0 && command.region_height == 0;
    if (command.operation == CaptureTargetOperation::remove)
    {
        return command.native_handle == 0U && empty_region
            ? std::optional<CaptureTargetCommand>(command)
            : std::nullopt;
    }

    const bool valid_surface_target =
        (command.kind == CaptureTargetKind::window ||
            command.kind == CaptureTargetKind::monitor) &&
        command.native_handle != 0U && empty_region;
    const bool valid_desktop_region = command.kind == CaptureTargetKind::desktop_region &&
        command.native_handle == 0U && command.region_width > 0 &&
        command.region_width <= 16'384 && command.region_height > 0 &&
        command.region_height <= 16'384;
    return valid_surface_target || valid_desktop_region
        ? std::optional<CaptureTargetCommand>(command)
        : std::nullopt;
}

std::optional<ManualOcrRequest> parse_manual_ocr_request(
    const std::span<const std::byte> bytes) noexcept
{
    constexpr std::size_t header_bytes = 8U;
    constexpr std::size_t target_instance_id_bytes = 16U;
    constexpr std::size_t maximum_explicit_targets = 8U;
    constexpr std::size_t maximum_payload_bytes =
        header_bytes + maximum_explicit_targets * target_instance_id_bytes;
    constexpr std::uint32_t schema_version = 2U;
    constexpr std::byte manual_ocr_operation{1U};
    if (bytes.size() < header_bytes || bytes.size() > maximum_payload_bytes ||
        read_u32(bytes, 0U) != schema_version || bytes[4U] != manual_ocr_operation)
        return std::nullopt;

    const ManualOcrScope scope = static_cast<ManualOcrScope>(
        std::to_integer<std::uint8_t>(bytes[5U]));
    const std::size_t target_count = read_u16(bytes, 6U);
    if (bytes.size() != header_bytes + target_count * target_instance_id_bytes ||
        (scope == ManualOcrScope::all_targets && target_count != 0U) ||
        (scope == ManualOcrScope::explicit_targets &&
            (target_count == 0U || target_count > maximum_explicit_targets)) ||
        (scope != ManualOcrScope::all_targets &&
            scope != ManualOcrScope::explicit_targets))
        return std::nullopt;

    try
    {
        ManualOcrRequest result{};
        result.scope = scope;
        result.target_instance_ids.reserve(target_count);
        for (std::size_t index{}; index < target_count; ++index)
        {
            std::array<std::byte, target_instance_id_bytes> identity{};
            std::ranges::copy(bytes.subspan(
                header_bytes + index * target_instance_id_bytes,
                target_instance_id_bytes), identity.begin());
            if (!unique_identity(result.target_instance_ids, identity))
                return std::nullopt;
        }
        return result;
    }
    catch (...)
    {
        return std::nullopt;
    }
}

std::optional<overlay::desired_state> parse_overlay_desired_state(
    const std::span<const std::byte> bytes) noexcept
{
    constexpr std::size_t header_bytes = 48U;
    constexpr std::size_t region_bytes = 136U;
    constexpr std::size_t slot_bytes = 40U;
    constexpr std::uint32_t maximum_regions = 256U;
    constexpr std::uint32_t maximum_slots = 4U;
    constexpr std::size_t maximum_characters = 16'384U;
    if (bytes.size() < header_bytes || read_u32(bytes, 0U) != 4U)
        return std::nullopt;
    const std::uint32_t region_count = read_u32(bytes, 4U);
    overlay::desired_state result{};
    std::ranges::copy(bytes.subspan(8U, 16U), result.runtime_epoch.begin());
    std::ranges::copy(bytes.subspan(24U, 16U), result.target_instance.begin());
    result.revision = read_u64(bytes, 40U);
    if (region_count > maximum_regions || !nonzero(result.runtime_epoch) ||
        !nonzero(result.target_instance) || result.revision == 0U ||
        result.revision > static_cast<std::uint64_t>(INT64_MAX))
        return std::nullopt;

    std::size_t offset = header_bytes;
    std::size_t total_characters{};
    std::vector<overlay::identity> region_ids;
    result.regions.reserve(region_count);
    for (std::uint32_t region_index{}; region_index < region_count; ++region_index)
    {
        if (offset > bytes.size() || region_bytes > bytes.size() - offset)
            return std::nullopt;
        overlay::region region{};
        std::ranges::copy(bytes.subspan(offset, 16U), region.id.begin());
        if (!unique_identity(region_ids, region.id) ||
            !reserved_zero(bytes.subspan(offset + 34U, 2U)))
            return std::nullopt;
        const std::int32_t x = read_i32(bytes, offset + 16U);
        const std::int32_t y = read_i32(bytes, offset + 20U);
        const std::int32_t width = read_i32(bytes, offset + 24U);
        const std::int32_t height = read_i32(bytes, offset + 28U);
        region.bounds = {static_cast<float>(x), static_cast<float>(y),
            static_cast<float>(width), static_cast<float>(height)};
        const std::uint8_t background = std::to_integer<std::uint8_t>(bytes[offset + 32U]);
        const std::uint8_t alignment = std::to_integer<std::uint8_t>(bytes[offset + 33U]);
        const double opacity = read_f64(bytes, offset + 44U);
        const double blur = read_f64(bytes, offset + 52U);
        const double padding = read_f64(bytes, offset + 60U);
        const double preferred_font = read_f64(bytes, offset + 68U);
        const double minimum_font = read_f64(bytes, offset + 76U);
        const std::uint32_t maximum_lines = read_u32(bytes, offset + 84U);
        const std::uint32_t slot_count = read_u32(bytes, offset + 88U);
        const std::int32_t destination_x = read_i32(bytes, offset + 92U);
        const std::int32_t destination_y = read_i32(bytes, offset + 96U);
        const std::int32_t destination_width = read_i32(bytes, offset + 100U);
        const std::int32_t destination_height = read_i32(bytes, offset + 104U);
        const double outline_width = read_f64(bytes, offset + 112U);
        const std::uint32_t minimum_dwell = read_u32(bytes, offset + 120U);
        const std::uint32_t crossfade = read_u32(bytes, offset + 124U);
        const bool automatic_shrink = bytes[offset + 129U] == std::byte{1};
        const bool no_scroll_overflow = bytes[offset + 130U] == std::byte{1};
        const std::uint32_t maximum_height = read_u32(bytes, offset + 132U);
        const bool destination_required = background ==
                static_cast<std::uint8_t>(overlay::background_mode::offset) ||
            background == static_cast<std::uint8_t>(overlay::background_mode::floating_panel);
        const bool destination_empty = destination_x == 0 && destination_y == 0 &&
            destination_width == 0 && destination_height == 0;
        const bool destination_valid = destination_width > 0 && destination_height > 0 &&
            destination_width <= 16'384 && destination_height <= 16'384;
        if (background > static_cast<std::uint8_t>(overlay::background_mode::no_cover) ||
            alignment > static_cast<std::uint8_t>(overlay::text_alignment::right) ||
            !std::isfinite(opacity) || opacity < 0.0 || opacity > 1.0 ||
            !std::isfinite(blur) || blur < 0.0 || blur > 64.0 ||
            !std::isfinite(padding) || padding < 0.0 ||
            !std::isfinite(outline_width) || outline_width < 0.0 || outline_width > 8.0 ||
            !std::isfinite(preferred_font) || !std::isfinite(minimum_font) ||
            preferred_font <= 0.0 || minimum_font <= 0.0 ||
            preferred_font < minimum_font || maximum_lines == 0U ||
            width <= 0 || height <= 0 || width > 16'384 || height > 16'384 ||
            slot_count > maximum_slots ||
            minimum_dwell > 3'000U || crossfade > 500U ||
            bytes[offset + 128U] > std::byte{1} || bytes[offset + 129U] > std::byte{1} ||
            bytes[offset + 130U] != std::byte{1} || bytes[offset + 131U] != std::byte{0} ||
            maximum_height > 16'384U ||
            (destination_required ? !destination_valid : !destination_empty))
            return std::nullopt;
        region.style.background = static_cast<overlay::background_mode>(background);
        region.style.alignment = static_cast<overlay::text_alignment>(alignment);
        region.style.background_color = read_color(bytes, offset + 36U);
        region.style.text_color = read_color(bytes, offset + 40U);
        region.style.outline_color = read_color(bytes, offset + 108U);
        region.style.background_color.alpha *= static_cast<float>(opacity);
        region.style.padding = static_cast<float>(padding);
        region.style.preferred_font_size = static_cast<float>(preferred_font);
        region.style.minimum_font_size = static_cast<float>(minimum_font);
        region.style.blur_radius = static_cast<float>(blur);
        region.style.outline_width = static_cast<float>(outline_width);
        region.style.maximum_lines = maximum_lines;
        region.style.maximum_height = maximum_height;
        region.style.automatic_shrink = automatic_shrink;
        region.style.no_scroll_overflow = no_scroll_overflow;
        region.style.minimum_dwell_milliseconds = minimum_dwell;
        region.style.crossfade_milliseconds = crossfade;
        region.style.reduced_motion = bytes[offset + 128U] == std::byte{1};
        if (destination_required)
            region.destination_bounds = overlay::rect_f{
                static_cast<float>(destination_x),
                static_cast<float>(destination_y),
                static_cast<float>(destination_width),
                static_cast<float>(destination_height)};
        offset += region_bytes;

        std::vector<overlay::identity> slot_ids;
        std::vector<std::uint32_t> orders;
        region.ordered_slots.reserve(slot_count);
        for (std::uint32_t slot_index{}; slot_index < slot_count; ++slot_index)
        {
            if (offset > bytes.size() || slot_bytes > bytes.size() - offset)
                return std::nullopt;
            overlay::slot slot{};
            std::ranges::copy(bytes.subspan(offset, 16U), slot.id.begin());
            slot.order = read_u32(bytes, offset + 16U);
            const std::uint32_t state = read_u32(bytes, offset + 20U);
            const std::uint32_t label_bytes = read_u32(bytes, offset + 24U);
            const std::uint32_t text_bytes = read_u32(bytes, offset + 28U);
            slot.stage_index = read_u32(bytes, offset + 32U);
            const std::size_t content_bytes = static_cast<std::size_t>(label_bytes) +
                static_cast<std::size_t>(text_bytes);
            if (!unique_identity(slot_ids, slot.id) ||
                std::ranges::find(orders, slot.order) != orders.end() ||
                state > static_cast<std::uint32_t>(overlay::slot_state::cancelled) ||
                !reserved_zero(bytes.subspan(offset + 36U, 4U)) ||
                content_bytes > bytes.size() - offset - slot_bytes)
                return std::nullopt;
            orders.push_back(slot.order);
            const auto label = utf8_to_utf16(bytes.subspan(
                offset + slot_bytes, label_bytes));
            const auto text = utf8_to_utf16(bytes.subspan(
                offset + slot_bytes + label_bytes, text_bytes));
            if (!label.has_value() || !text.has_value()) return std::nullopt;
            total_characters += label->size() + text->size();
            if (total_characters > maximum_characters) return std::nullopt;
            slot.state = static_cast<overlay::slot_state>(state);
            slot.label = std::move(*label);
            slot.text = std::move(*text);
            region.ordered_slots.push_back(std::move(slot));
            offset += slot_bytes + content_bytes;
        }
        result.regions.push_back(std::move(region));
    }
    if (offset != bytes.size()) return std::nullopt;
    return result;
}

std::optional<PolicyRevisionCommand> parse_policy_revision(
    const std::span<const std::byte> bytes) noexcept
{
    constexpr std::size_t header_bytes = 48U;
    constexpr std::size_t entry_bytes = 24U;
    constexpr std::uint32_t maximum_regions = 256U;
    constexpr std::uint32_t maximum_policy_bytes = 256U;
    if (bytes.size() < header_bytes || read_u32(bytes, 0U) != 2U ||
        !reserved_zero(bytes.subspan(40U, 8U)))
        return std::nullopt;
    const std::uint32_t region_count = read_u32(bytes, 4U);
    PolicyRevisionCommand result{};
    result.revision = read_u64(bytes, 8U);
    result.profile_revision = read_u64(bytes, 16U);
    std::ranges::copy(bytes.subspan(24U, 16U), result.profile_id.begin());
    if (region_count > maximum_regions || result.revision == 0U ||
        result.profile_revision == 0U || !nonzero(result.profile_id) ||
        result.revision > static_cast<std::uint64_t>(INT64_MAX) ||
        result.profile_revision > static_cast<std::uint64_t>(INT64_MAX))
        return std::nullopt;
    result.regions.reserve(region_count);
    std::vector<std::array<std::byte, 16U>> region_ids;
    std::size_t offset = header_bytes;
    for (std::uint32_t index{}; index < region_count; ++index)
    {
        if (offset > bytes.size() || entry_bytes > bytes.size() - offset)
            return std::nullopt;
        PolicyRegionState region{};
        std::ranges::copy(bytes.subspan(offset, 16U), region.region_id.begin());
        const std::uint32_t policy_bytes = read_u32(bytes, offset + 16U);
        if (!unique_identity(region_ids, region.region_id) ||
            !reserved_zero(bytes.subspan(offset + 20U, 4U)) ||
            policy_bytes == 0U || policy_bytes > maximum_policy_bytes ||
            policy_bytes > bytes.size() - offset - entry_bytes)
            return std::nullopt;
        const auto policy = bytes.subspan(offset + entry_bytes, policy_bytes);
        if (!std::ranges::all_of(policy, [](const std::byte value)
            {
                const auto character = std::to_integer<std::uint8_t>(value);
                return character >= 0x20U && character <= 0x7eU;
            }))
            return std::nullopt;
        region.policy.assign(reinterpret_cast<const char*>(policy.data()), policy.size());
        result.regions.push_back(std::move(region));
        offset += entry_bytes + policy_bytes;
    }
    if (offset != bytes.size()) return std::nullopt;
    return result;
}

std::optional<ProcessingConfigurationCommand> parse_processing_configuration(
    const std::span<const std::byte> bytes) noexcept
{
    constexpr std::size_t header_bytes = 72U;
    constexpr std::size_t region_bytes = 80U;
    constexpr std::uint32_t maximum_regions = 256U;
    if (bytes.size() < header_bytes || read_u32(bytes, 0U) != 3U ||
        std::to_integer<std::uint8_t>(bytes[64U]) > 1U ||
        !reserved_zero(bytes.subspan(65U, 7U)))
        return std::nullopt;

    const std::uint32_t region_count = read_u32(bytes, 4U);
    ProcessingConfigurationCommand result{};
    result.configuration_revision = read_u64(bytes, 8U);
    result.profile_revision = read_u64(bytes, 16U);
    std::ranges::copy(bytes.subspan(24U, 16U), result.target_instance_id.begin());
    std::ranges::copy(bytes.subspan(40U, 16U), result.profile_id.begin());
    result.detection_long_edge = read_u32(bytes, 56U);
    result.remaining_area_interval_milliseconds = read_u32(bytes, 60U);
    result.scan_remaining_area = bytes[64U] == std::byte{1};
    if (region_count > maximum_regions || result.configuration_revision == 0U ||
        result.profile_revision == 0U || !nonzero(result.target_instance_id) ||
        !nonzero(result.profile_id) ||
        result.configuration_revision > static_cast<std::uint64_t>(INT64_MAX) ||
        result.profile_revision > static_cast<std::uint64_t>(INT64_MAX) ||
        result.detection_long_edge < 320U || result.detection_long_edge > 1920U ||
        result.remaining_area_interval_milliseconds < 100U ||
        result.remaining_area_interval_milliseconds > 3'600'000U)
        return std::nullopt;

    result.regions.reserve(region_count);
    std::vector<std::array<std::byte, 16U>> region_ids;
    std::size_t offset = header_bytes;
    for (std::uint32_t index{}; index < region_count; ++index)
    {
        if (offset > bytes.size() || region_bytes > bytes.size() - offset)
            return std::nullopt;
        ProcessingRegion region{};
        std::ranges::copy(bytes.subspan(offset, 16U), region.region_id.begin());
        region.x = read_f64(bytes, offset + 16U);
        region.y = read_f64(bytes, offset + 24U);
        region.width = read_f64(bytes, offset + 32U);
        region.height = read_f64(bytes, offset + 40U);
        region.priority = std::to_integer<std::uint8_t>(bytes[offset + 48U]);
        region.area_mode = std::to_integer<std::uint8_t>(bytes[offset + 49U]);
        region.lock_degradation = bytes[offset + 50U] == std::byte{1};
        const std::uint8_t flags = std::to_integer<std::uint8_t>(bytes[offset + 51U]);
        region.detect_orientation = (flags & 1U) != 0U;
        region.use_cloud_ocr = (flags & 2U) != 0U;
        region.recognition_interval_milliseconds = read_u32(bytes, offset + 52U);
        region.line_break_mode = std::to_integer<std::uint8_t>(bytes[offset + 56U]);
        const std::uint16_t provider_bytes = read_u16(bytes, offset + 58U);
        const std::uint16_t language_bytes = read_u16(bytes, offset + 60U);
        const std::uint16_t pipeline_bytes = read_u16(bytes, offset + 62U);
        region.detection_scale = read_f64(bytes, offset + 64U);
        region.cloud_consent_policy_revision = read_u64(bytes, offset + 72U);
        const std::size_t variable_bytes = static_cast<std::size_t>(provider_bytes) +
            language_bytes + pipeline_bytes;
        if (!unique_identity(region_ids, region.region_id) ||
            bytes[offset + 50U] > std::byte{1} || (flags & ~3U) != 0U ||
            bytes[offset + 57U] != std::byte{} || region.priority > 3U ||
            region.area_mode > 2U || region.line_break_mode > 4U ||
            region.recognition_interval_milliseconds < 16U ||
            region.recognition_interval_milliseconds > 3'600'000U ||
            !std::isfinite(region.x) || !std::isfinite(region.y) ||
            !std::isfinite(region.width) || !std::isfinite(region.height) ||
            region.x < 0.0 || region.y < 0.0 || region.width <= 0.0 ||
            region.height <= 0.0 || region.x + region.width > 1.0 ||
            region.y + region.height > 1.0 ||
            (region.use_cloud_ocr != (region.cloud_consent_policy_revision > 0U)) ||
            region.cloud_consent_policy_revision > static_cast<std::uint64_t>(INT64_MAX) ||
            !std::isfinite(region.detection_scale) || region.detection_scale < 0.1 ||
            region.detection_scale > 4.0 || provider_bytes == 0U ||
            provider_bytes > 128U || language_bytes == 0U || language_bytes > 64U ||
            pipeline_bytes > 512U || variable_bytes > bytes.size() - offset - region_bytes)
            return std::nullopt;

        const auto read_text = [&](const std::size_t text_offset,
            const std::size_t text_bytes) -> std::optional<std::string>
        {
            const auto source = bytes.subspan(text_offset, text_bytes);
            const auto decoded = utf8_to_utf16(source);
            if (!decoded.has_value() || std::ranges::any_of(*decoded, [](const char16_t value)
                { return value < 0x20U || value == 0x7fU; }))
                return std::nullopt;
            return std::string(reinterpret_cast<const char*>(source.data()), source.size());
        };
        std::size_t text_offset = offset + region_bytes;
        auto provider = read_text(text_offset, provider_bytes);
        text_offset += provider_bytes;
        auto language = read_text(text_offset, language_bytes);
        text_offset += language_bytes;
        auto pipeline = read_text(text_offset, pipeline_bytes);
        if (!provider.has_value() || !language.has_value() || !pipeline.has_value())
            return std::nullopt;
        region.ocr_provider_id = std::move(*provider);
        region.recognition_language = std::move(*language);
        region.preprocessing_pipeline = std::move(*pipeline);
        result.regions.push_back(std::move(region));
        offset += region_bytes + variable_bytes;
    }
    return offset == bytes.size()
        ? std::optional<ProcessingConfigurationCommand>(std::move(result))
        : std::nullopt;
}

std::optional<ThumbnailRequest> parse_thumbnail_request(
    const std::span<const std::byte> bytes) noexcept
{
    if (bytes.size() != 24U || read_u32(bytes, 0U) != 1U)
        return std::nullopt;
    ThumbnailRequest request{};
    request.maximum_long_edge = read_u32(bytes, 4U);
    std::ranges::copy(bytes.subspan(8U, 16U), request.target_instance_id.begin());
    if (!nonzero(request.target_instance_id) ||
        request.maximum_long_edge < 320U ||
        request.maximum_long_edge > 1280U)
        return std::nullopt;
    return request;
}

std::optional<OcrResultCommand> parse_ocr_result(
    const std::span<const std::byte> bytes) noexcept
{
    constexpr std::size_t fixed_bytes = 144U;
    constexpr std::size_t line_bytes = 48U;
    constexpr std::uint32_t maximum_lines = 2'048U;
    if (bytes.size() < fixed_bytes || read_u32(bytes, 0U) != 1U ||
        read_u32(bytes, 4U) > maximum_lines || bytes[120U] > std::byte{1} ||
        !reserved_zero(bytes.subspan(121U, 7U)) ||
        !reserved_zero(bytes.subspan(140U, 4U)) ||
        bytes[41U] > std::byte{1} ||
        !reserved_zero(bytes.subspan(42U, 2U)))
        return std::nullopt;

    OcrResultCommand result{};
    std::ranges::copy(bytes.subspan(8U, 16U), result.token.runtime_epoch.begin());
    std::ranges::copy(bytes.subspan(24U, 16U), result.token.target_instance_id.begin());
    result.token.area_kind = std::to_integer<std::uint8_t>(bytes[40U]);
    result.token.manual = bytes[41U] == std::byte{1};
    std::ranges::copy(bytes.subspan(44U, 16U), result.token.region_id.begin());
    std::ranges::copy(bytes.subspan(60U, 16U), result.token.text_track_id.begin());
    result.token.source_generation = read_u64(bytes, 76U);
    result.token.profile_revision = read_u64(bytes, 84U);
    std::ranges::copy(bytes.subspan(92U, 16U), result.token.ocr_run_id.begin());
    result.token.attempt = read_u32(bytes, 108U);
    result.token.result_sequence = read_u64(bytes, 112U);
    result.stable = bytes[120U] == std::byte{1};
    const std::uint32_t model_id_bytes = read_u32(bytes, 128U);
    const std::uint32_t model_version_bytes = read_u32(bytes, 132U);
    const std::uint32_t error_bytes = read_u32(bytes, 136U);
    const bool region_identity_valid = result.token.area_kind == 0U
        ? nonzero(result.token.region_id)
        : reserved_zero(result.token.region_id);
    if (!nonzero(result.token.runtime_epoch) ||
        !nonzero(result.token.target_instance_id) ||
        result.token.area_kind > 2U || !region_identity_valid ||
        !nonzero(result.token.text_track_id) || !nonzero(result.token.ocr_run_id) ||
        result.token.source_generation == 0U || result.token.profile_revision == 0U ||
        result.token.attempt == 0U || result.token.result_sequence == 0U ||
        result.token.source_generation > static_cast<std::uint64_t>(INT64_MAX) ||
        result.token.profile_revision > static_cast<std::uint64_t>(INT64_MAX) ||
        result.token.result_sequence > static_cast<std::uint64_t>(INT64_MAX) ||
        model_id_bytes == 0U || model_id_bytes > 128U ||
        model_version_bytes == 0U || model_version_bytes > 128U ||
        error_bytes > 128U)
        return std::nullopt;

    const std::size_t metadata_bytes = static_cast<std::size_t>(model_id_bytes) +
        model_version_bytes + error_bytes;
    if (metadata_bytes > bytes.size() - fixed_bytes) return std::nullopt;
    std::size_t offset = fixed_bytes;
    const auto read_metadata = [&](const std::size_t length)
        -> std::optional<std::u16string>
    {
        const auto decoded = utf8_to_utf16(bytes.subspan(offset, length));
        if (!decoded.has_value() || std::ranges::any_of(*decoded,
            [](const char16_t value) { return value < 0x20U || value == 0x7fU; }))
            return std::nullopt;
        offset += length;
        return decoded;
    };
    auto model_id = read_metadata(model_id_bytes);
    auto model_version = read_metadata(model_version_bytes);
    if (!model_id.has_value() || !model_version.has_value()) return std::nullopt;
    result.model_id = std::move(*model_id);
    result.model_version = std::move(*model_version);
    const auto error = bytes.subspan(offset, error_bytes);
    if (!std::ranges::all_of(error, [](const std::byte value)
        {
            const char character = static_cast<char>(std::to_integer<std::uint8_t>(value));
            return (character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9') || character == '.' ||
                character == '_' || character == '-';
        }))
        return std::nullopt;
    result.terminal_error_code.assign(
        reinterpret_cast<const char*>(error.data()), error.size());
    offset += error_bytes;

    const std::uint32_t line_count = read_u32(bytes, 4U);
    result.lines.reserve(line_count);
    for (std::uint32_t index{}; index < line_count; ++index)
    {
        if (offset > bytes.size() || line_bytes > bytes.size() - offset)
            return std::nullopt;
        const std::uint32_t text_bytes = read_u32(bytes, offset);
        const auto orientation = std::bit_cast<std::int16_t>(
            read_u16(bytes, offset + 4U));
        if (orientation < -180 || orientation > 180 ||
            bytes[offset + 6U] > std::byte{1} || bytes[offset + 7U] != std::byte{} ||
            text_bytes > bytes.size() - offset - line_bytes)
            return std::nullopt;
        OcrResultLine line{};
        line.orientation_degrees = orientation;
        line.vertical = bytes[offset + 6U] == std::byte{1};
        line.x = read_f64(bytes, offset + 8U);
        line.y = read_f64(bytes, offset + 16U);
        line.width = read_f64(bytes, offset + 24U);
        line.height = read_f64(bytes, offset + 32U);
        line.confidence = read_f64(bytes, offset + 40U);
        const auto text = utf8_to_utf16(
            bytes.subspan(offset + line_bytes, text_bytes));
        if (!text.has_value() || !std::isfinite(line.x) || !std::isfinite(line.y) ||
            !std::isfinite(line.width) || !std::isfinite(line.height) ||
            !std::isfinite(line.confidence) || line.x < 0.0 || line.y < 0.0 ||
            line.width <= 0.0 || line.height <= 0.0 || line.x + line.width > 1.0 ||
            line.y + line.height > 1.0 || line.confidence < 0.0 ||
            line.confidence > 1.0)
            return std::nullopt;
        line.text = std::move(*text);
        result.lines.push_back(std::move(line));
        offset += line_bytes + text_bytes;
    }
    return offset == bytes.size()
        ? std::optional<OcrResultCommand>(std::move(result))
        : std::nullopt;
}

std::optional<std::vector<std::byte>> encode_cloud_ocr_crop_request(
    const CloudOcrCropEvent& event) noexcept
{
    constexpr std::size_t fixed_bytes = 160U;
    constexpr std::size_t maximum_payload_bytes = 8'388'608U - 56U;
    const bool region_identity_valid = event.token.area_kind == 0U
        ? nonzero(event.token.region_id)
        : reserved_zero(event.token.region_id);
    const auto valid_identifier = [](const std::string_view value)
    {
        return !value.empty() && value.size() <= 128U &&
            std::ranges::all_of(value, [](const char character)
            {
                return (character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') || character == '.' ||
                    character == '_' || character == '-';
            });
    };
    const bool valid_mime = event.mime_type.starts_with("image/") &&
        event.mime_type.size() > 6U && event.mime_type.size() <= 64U &&
        std::ranges::all_of(event.mime_type.substr(6U), [](const char character)
        {
            return character >= 'a' && character <= 'z';
        });
    if (!nonzero(event.token.runtime_epoch) ||
        !nonzero(event.token.target_instance_id) || event.token.area_kind > 2U ||
        !region_identity_valid || !nonzero(event.token.text_track_id) ||
        !nonzero(event.token.ocr_run_id) || event.token.source_generation == 0U ||
        event.token.profile_revision == 0U || event.token.attempt == 0U ||
        event.token.result_sequence == 0U || event.consent_policy_revision == 0U ||
        event.deadline_utc_ticks == 0U || event.pixel_width == 0U ||
        event.pixel_height == 0U || !valid_mime || !valid_identifier(event.provider_id) ||
        event.encoded_crop.empty() || event.encoded_byte_ceiling == 0U ||
        event.encoded_crop.size() > event.encoded_byte_ceiling)
        return std::nullopt;
    const std::size_t variable_bytes = event.mime_type.size() + event.provider_id.size() +
        event.encoded_crop.size();
    if (variable_bytes > maximum_payload_bytes - fixed_bytes) return std::nullopt;
    try
    {
        std::vector<std::byte> payload(fixed_bytes + variable_bytes);
        const auto bytes = std::span<std::byte>(payload);
        write_u32(bytes, 0U, 1U);
        std::ranges::copy(event.token.runtime_epoch, payload.begin() + 4U);
        std::ranges::copy(event.token.target_instance_id, payload.begin() + 20U);
        payload[36U] = static_cast<std::byte>(event.token.area_kind);
        payload[37U] = event.token.manual ? std::byte{1} : std::byte{};
        std::ranges::copy(event.token.region_id, payload.begin() + 40U);
        std::ranges::copy(event.token.text_track_id, payload.begin() + 56U);
        write_u64(bytes, 72U, event.token.source_generation);
        write_u64(bytes, 80U, event.token.profile_revision);
        std::ranges::copy(event.token.ocr_run_id, payload.begin() + 88U);
        write_u32(bytes, 104U, event.token.attempt);
        write_u64(bytes, 108U, event.token.result_sequence);
        write_u32(bytes, 116U, static_cast<std::uint32_t>(event.mime_type.size()));
        write_u64(bytes, 120U, event.consent_policy_revision);
        write_u64(bytes, 128U, event.deadline_utc_ticks);
        write_u32(bytes, 136U, event.pixel_width);
        write_u32(bytes, 140U, event.pixel_height);
        write_u32(bytes, 144U, event.encoded_byte_ceiling);
        write_u32(bytes, 148U, static_cast<std::uint32_t>(event.encoded_crop.size()));
        payload[152U] = std::byte{1};
        write_u32(bytes, 156U, static_cast<std::uint32_t>(event.provider_id.size()));
        std::size_t offset = fixed_bytes;
        std::ranges::transform(event.mime_type, payload.begin() + offset,
            [](const char value) { return static_cast<std::byte>(value); });
        offset += event.mime_type.size();
        std::ranges::transform(event.provider_id, payload.begin() + offset,
            [](const char value) { return static_cast<std::byte>(value); });
        offset += event.provider_id.size();
        std::ranges::copy(event.encoded_crop, payload.begin() + offset);
        return payload;
    }
    catch (...)
    {
        return std::nullopt;
    }
}

std::optional<std::vector<std::byte>> encode_ocr_result(
    const OcrResultCommand& result) noexcept
{
    constexpr std::size_t fixed_bytes = 144U;
    constexpr std::size_t line_bytes = 48U;
    constexpr std::size_t maximum_payload_bytes = 8'388'608U - 56U;
    if (result.lines.size() > 2'048U || !nonzero(result.token.runtime_epoch) ||
        !nonzero(result.token.target_instance_id) || result.token.area_kind > 2U ||
        (result.token.area_kind == 0U ? !nonzero(result.token.region_id)
            : !reserved_zero(result.token.region_id)) ||
        !nonzero(result.token.text_track_id) || !nonzero(result.token.ocr_run_id) ||
        result.token.source_generation == 0U || result.token.profile_revision == 0U ||
        result.token.attempt == 0U || result.token.result_sequence == 0U ||
        result.token.source_generation > static_cast<std::uint64_t>(INT64_MAX) ||
        result.token.profile_revision > static_cast<std::uint64_t>(INT64_MAX) ||
        result.token.result_sequence > static_cast<std::uint64_t>(INT64_MAX) ||
        result.terminal_error_code.size() > 128U)
        return std::nullopt;
    const auto valid_code = [](const std::string_view value)
    {
        return std::ranges::all_of(value, [](const char character)
        {
            return (character >= 'a' && character <= 'z') ||
                (character >= 'A' && character <= 'Z') ||
                (character >= '0' && character <= '9') || character == '.' ||
                character == '_' || character == '-';
        });
    };
    if (!valid_code(result.terminal_error_code)) return std::nullopt;
    const auto model_id = utf16_to_utf8(result.model_id);
    const auto model_version = utf16_to_utf8(result.model_version);
    if (!model_id.has_value() || !model_version.has_value() || model_id->empty() ||
        model_version->empty() || model_id->size() > 128U || model_version->size() > 128U ||
        std::ranges::any_of(*model_id,
            [](const unsigned char value) { return value < 0x20U || value == 0x7FU; }) ||
        std::ranges::any_of(*model_version,
            [](const unsigned char value) { return value < 0x20U || value == 0x7FU; }))
        return std::nullopt;
    std::vector<std::string> texts;
    texts.reserve(result.lines.size());
    std::size_t payload_bytes = fixed_bytes + model_id->size() + model_version->size() +
        result.terminal_error_code.size();
    for (const OcrResultLine& line : result.lines)
    {
        const auto text = utf16_to_utf8(line.text);
        if (!text.has_value() || !std::isfinite(line.x) || !std::isfinite(line.y) ||
            !std::isfinite(line.width) || !std::isfinite(line.height) ||
            !std::isfinite(line.confidence) || line.x < 0.0 || line.y < 0.0 ||
            line.width <= 0.0 || line.height <= 0.0 || line.x + line.width > 1.0 ||
            line.y + line.height > 1.0 || line.confidence < 0.0 ||
            line.confidence > 1.0 || line.orientation_degrees < -180 ||
            line.orientation_degrees > 180 || text->size() > maximum_payload_bytes)
            return std::nullopt;
        if (payload_bytes > maximum_payload_bytes - line_bytes - text->size())
            return std::nullopt;
        payload_bytes += line_bytes + text->size();
        texts.push_back(std::move(*text));
    }
    try
    {
        std::vector<std::byte> payload(payload_bytes);
        const auto bytes = std::span<std::byte>(payload);
        write_u32(bytes, 0U, 1U);
        write_u32(bytes, 4U, static_cast<std::uint32_t>(result.lines.size()));
        std::ranges::copy(result.token.runtime_epoch, payload.begin() + 8U);
        std::ranges::copy(result.token.target_instance_id, payload.begin() + 24U);
        payload[40U] = static_cast<std::byte>(result.token.area_kind);
        payload[41U] = result.token.manual ? std::byte{1} : std::byte{};
        std::ranges::copy(result.token.region_id, payload.begin() + 44U);
        std::ranges::copy(result.token.text_track_id, payload.begin() + 60U);
        write_u64(bytes, 76U, result.token.source_generation);
        write_u64(bytes, 84U, result.token.profile_revision);
        std::ranges::copy(result.token.ocr_run_id, payload.begin() + 92U);
        write_u32(bytes, 108U, result.token.attempt);
        write_u64(bytes, 112U, result.token.result_sequence);
        payload[120U] = result.stable ? std::byte{1} : std::byte{};
        write_u32(bytes, 128U, static_cast<std::uint32_t>(model_id->size()));
        write_u32(bytes, 132U, static_cast<std::uint32_t>(model_version->size()));
        write_u32(bytes, 136U,
            static_cast<std::uint32_t>(result.terminal_error_code.size()));
        std::size_t offset = fixed_bytes;
        const auto copy_text = [&](const std::string_view text)
        {
            std::ranges::transform(text, payload.begin() + offset,
                [](const char value) { return static_cast<std::byte>(value); });
            offset += text.size();
        };
        copy_text(*model_id);
        copy_text(*model_version);
        copy_text(result.terminal_error_code);
        for (std::size_t index{}; index < result.lines.size(); ++index)
        {
            const OcrResultLine& line = result.lines[index];
            write_u32(bytes, offset, static_cast<std::uint32_t>(texts[index].size()));
            write_u16(bytes, offset + 4U, std::bit_cast<std::uint16_t>(
                static_cast<std::int16_t>(line.orientation_degrees)));
            payload[offset + 6U] = line.vertical ? std::byte{1} : std::byte{};
            write_u64(bytes, offset + 8U, std::bit_cast<std::uint64_t>(line.x));
            write_u64(bytes, offset + 16U, std::bit_cast<std::uint64_t>(line.y));
            write_u64(bytes, offset + 24U, std::bit_cast<std::uint64_t>(line.width));
            write_u64(bytes, offset + 32U, std::bit_cast<std::uint64_t>(line.height));
            write_u64(bytes, offset + 40U, std::bit_cast<std::uint64_t>(line.confidence));
            offset += line_bytes;
            copy_text(texts[index]);
        }
        return payload;
    }
    catch (...)
    {
        return std::nullopt;
    }
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
