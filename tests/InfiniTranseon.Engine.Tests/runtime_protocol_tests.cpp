#include <algorithm>
#include <array>
#include <bit>
#include <cstddef>
#include <cstdint>
#include <cstdlib>
#include <iostream>
#include <source_location>
#include <span>
#include <string>
#include <vector>

#include <infini_runtime_protocol.h>
#include <runtime_capture_controller.h>

namespace
{
using infini::runtime::BootstrapConfig;
using infini::runtime::ProtocolError;

void require(
    const bool condition,
    const std::source_location location = std::source_location::current())
{
    if (!condition)
    {
        std::cerr << "Assertion failed at line " << location.line() << '\n';
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

std::uint32_t read_u32(std::span<const std::byte> bytes, const std::size_t offset)
{
    std::uint32_t value{};
    for (std::size_t index{}; index < sizeof(value); ++index)
        value |= std::to_integer<std::uint32_t>(bytes[offset + index]) << (index * 8U);
    return value;
}

std::uint64_t read_u64(std::span<const std::byte> bytes, const std::size_t offset)
{
    std::uint64_t value{};
    for (std::size_t index{}; index < sizeof(value); ++index)
        value |= std::to_integer<std::uint64_t>(bytes[offset + index]) << (index * 8U);
    return value;
}

void write_u16(std::span<std::byte> bytes, const std::size_t offset,
    const std::uint16_t value)
{
    bytes[offset] = static_cast<std::byte>(value & 0xffU);
    bytes[offset + 1U] = static_cast<std::byte>((value >> 8U) & 0xffU);
}

void write_u64(std::span<std::byte> bytes, const std::size_t offset,
    const std::uint64_t value)
{
    for (std::size_t index = 0; index < sizeof(value); ++index)
    {
        bytes[offset + index] = static_cast<std::byte>((value >> (index * 8U)) & 0xffU);
    }
}

void write_f64(std::span<std::byte> bytes, const std::size_t offset,
    const double value)
{
    write_u64(bytes, offset, std::bit_cast<std::uint64_t>(value));
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

std::array<std::byte, 72U> valid_capture_target_command()
{
    std::array<std::byte, 72U> payload{};
    write_u32(payload, 0U, 1U);
    payload[4U] = std::byte{1};
    payload[5U] = std::byte{1};
    write_u64(payload, 8U, 7U);
    payload[16U] = std::byte{1};
    payload[32U] = std::byte{2};
    write_u64(payload, 48U, 0x1234U);
    return payload;
}

std::vector<std::byte> valid_overlay_command()
{
    constexpr std::string_view label = "primary";
    constexpr std::string_view text = "translated";
    std::vector<std::byte> payload(48U + 136U + 40U + label.size() + text.size());
    const auto bytes = std::span<std::byte>(payload);
    write_u32(bytes, 0U, 4U);
    write_u32(bytes, 4U, 1U);
    bytes[8U] = std::byte{1};
    bytes[24U] = std::byte{2};
    write_u64(bytes, 40U, 9U);
    constexpr std::size_t region = 48U;
    bytes[region] = std::byte{3};
    write_u32(bytes, region + 16U, 12U);
    write_u32(bytes, region + 20U, 34U);
    write_u32(bytes, region + 24U, 900U);
    write_u32(bytes, region + 28U, 240U);
    bytes[region + 32U] = std::byte{2};
    bytes[region + 33U] = std::byte{1};
    bytes[region + 36U] = std::byte{0x11};
    bytes[region + 37U] = std::byte{0x22};
    bytes[region + 38U] = std::byte{0x33};
    bytes[region + 39U] = std::byte{0xcc};
    bytes[region + 40U] = std::byte{0xee};
    bytes[region + 41U] = std::byte{0xdd};
    bytes[region + 42U] = std::byte{0xcc};
    bytes[region + 43U] = std::byte{0xff};
    write_f64(bytes, region + 44U, 0.75);
    write_f64(bytes, region + 52U, 14.0);
    write_f64(bytes, region + 60U, 11.0);
    write_f64(bytes, region + 68U, 30.0);
    write_f64(bytes, region + 76U, 15.0);
    write_u32(bytes, region + 84U, 5U);
    write_u32(bytes, region + 88U, 1U);
    write_u32(bytes, region + 92U, 1000U);
    write_u32(bytes, region + 96U, 100U);
    write_u32(bytes, region + 100U, 600U);
    write_u32(bytes, region + 104U, 300U);
    bytes[region + 108U] = std::byte{0x01};
    bytes[region + 109U] = std::byte{0x02};
    bytes[region + 110U] = std::byte{0x03};
    bytes[region + 111U] = std::byte{0xff};
    write_f64(bytes, region + 112U, 1.5);
    write_u32(bytes, region + 120U, 650U);
    write_u32(bytes, region + 124U, 140U);
    bytes[region + 128U] = std::byte{1};
    bytes[region + 129U] = std::byte{1};
    bytes[region + 130U] = std::byte{1};
    write_u32(bytes, region + 132U, 180U);
    constexpr std::size_t slot = region + 136U;
    bytes[slot] = std::byte{4};
    write_u32(bytes, slot + 20U, 2U);
    write_u32(bytes, slot + 24U, static_cast<std::uint32_t>(label.size()));
    write_u32(bytes, slot + 28U, static_cast<std::uint32_t>(text.size()));
    write_u32(bytes, slot + 32U, 2U);
    std::ranges::transform(label, payload.begin() + slot + 40U,
        [](const char value) { return static_cast<std::byte>(value); });
    std::ranges::transform(text, payload.begin() + slot + 40U + label.size(),
        [](const char value) { return static_cast<std::byte>(value); });
    return payload;
}

std::vector<std::byte> valid_policy_revision()
{
    constexpr std::string_view policy = "3:paused";
    std::vector<std::byte> payload(48U + 24U + policy.size());
    const auto bytes = std::span<std::byte>(payload);
    write_u32(bytes, 0U, 2U);
    write_u32(bytes, 4U, 1U);
    write_u64(bytes, 8U, 12U);
    write_u64(bytes, 16U, 4U);
    bytes[24U] = std::byte{6};
    bytes[48U] = std::byte{7};
    write_u32(bytes, 64U, static_cast<std::uint32_t>(policy.size()));
    std::ranges::transform(policy, payload.begin() + 72U,
        [](const char value) { return static_cast<std::byte>(value); });
    return payload;
}

std::vector<std::byte> valid_processing_configuration()
{
    constexpr std::string_view provider = "ocr.windows.media";
    constexpr std::string_view language = "ja-JP";
    constexpr std::string_view pipeline = "grayscale|threshold";
    std::vector<std::byte> payload(
        72U + 80U + provider.size() + language.size() + pipeline.size());
    const auto bytes = std::span<std::byte>(payload);
    write_u32(bytes, 0U, 3U);
    write_u32(bytes, 4U, 1U);
    write_u64(bytes, 8U, 9U);
    write_u64(bytes, 16U, 4U);
    bytes[24U] = std::byte{5};
    bytes[40U] = std::byte{6};
    write_u32(bytes, 56U, 1920U);
    write_u32(bytes, 60U, 1000U);
    bytes[64U] = std::byte{1};
    constexpr std::size_t region = 72U;
    bytes[region] = std::byte{7};
    write_f64(bytes, region + 16U, 0.1);
    write_f64(bytes, region + 24U, 0.6);
    write_f64(bytes, region + 32U, 0.8);
    write_f64(bytes, region + 40U, 0.3);
    bytes[region + 48U] = std::byte{0};
    bytes[region + 49U] = std::byte{0};
    bytes[region + 50U] = std::byte{1};
    bytes[region + 51U] = std::byte{1};
    write_u32(bytes, region + 52U, 125U);
    bytes[region + 56U] = std::byte{2};
    write_u16(bytes, region + 58U, static_cast<std::uint16_t>(provider.size()));
    write_u16(bytes, region + 60U, static_cast<std::uint16_t>(language.size()));
    write_u16(bytes, region + 62U, static_cast<std::uint16_t>(pipeline.size()));
    write_f64(bytes, region + 64U, 0.75);
    std::size_t offset = region + 80U;
    for (const std::string_view text : {provider, language, pipeline})
    {
        std::ranges::transform(text, payload.begin() + offset,
            [](const char value) { return static_cast<std::byte>(value); });
        offset += text.size();
    }
    return payload;
}

std::vector<std::byte> valid_ocr_result()
{
    constexpr std::string_view model = "paddle-ja";
    constexpr std::string_view version = "1.2.0";
    constexpr std::string_view text = "attack 100";
    std::vector<std::byte> payload(
        144U + model.size() + version.size() + 48U + text.size());
    const auto bytes = std::span<std::byte>(payload);
    write_u32(bytes, 0U, 1U);
    write_u32(bytes, 4U, 1U);
    bytes[8U] = std::byte{1};
    bytes[24U] = std::byte{2};
    bytes[40U] = std::byte{0};
    bytes[44U] = std::byte{3};
    bytes[60U] = std::byte{4};
    write_u64(bytes, 76U, 5U);
    write_u64(bytes, 84U, 6U);
    bytes[92U] = std::byte{7};
    write_u32(bytes, 108U, 1U);
    write_u64(bytes, 112U, 8U);
    bytes[120U] = std::byte{1};
    write_u32(bytes, 128U, static_cast<std::uint32_t>(model.size()));
    write_u32(bytes, 132U, static_cast<std::uint32_t>(version.size()));
    std::size_t offset = 144U;
    std::ranges::transform(model, payload.begin() + offset,
        [](const char value) { return static_cast<std::byte>(value); });
    offset += model.size();
    std::ranges::transform(version, payload.begin() + offset,
        [](const char value) { return static_cast<std::byte>(value); });
    offset += version.size();
    write_u32(bytes, offset, static_cast<std::uint32_t>(text.size()));
    write_u16(bytes, offset + 4U, 90U);
    bytes[offset + 6U] = std::byte{1};
    write_f64(bytes, offset + 8U, 0.1);
    write_f64(bytes, offset + 16U, 0.2);
    write_f64(bytes, offset + 24U, 0.3);
    write_f64(bytes, offset + 32U, 0.4);
    write_f64(bytes, offset + 40U, 0.95);
    std::ranges::transform(text, payload.begin() + offset + 48U,
        [](const char value) { return static_cast<std::byte>(value); });
    return payload;
}
}

int main()
{
    require(infini::runtime::calculate_gpu_pool_limit(800U) == 200U);
    require(infini::runtime::calculate_gpu_pool_limit(8ULL * 1024U * 1024U * 1024U) ==
        1024ULL * 1024U * 1024U);
    require(infini::runtime::runtime_capacity_available(7U, 1U, 8U));
    require(!infini::runtime::runtime_capacity_available(8U, 1U, 8U));
    require(!infini::runtime::runtime_capacity_available(7U, 2U, 8U));
    require(!infini::runtime::manual_ocr_allows_region(
        false, true, true, false));
    require(!infini::runtime::manual_ocr_allows_region(
        false, false, false, false));
    require(!infini::runtime::manual_ocr_allows_region(
        false, false, true, true));
    require(infini::runtime::manual_ocr_allows_region(
        true, true, false, false));
    require(!infini::runtime::manual_ocr_allows_region(
        true, true, false, true));
    require(infini::runtime::manual_ocr_target_available(1U, true));
    require(infini::runtime::manual_ocr_target_available(4U, true));
    require(infini::runtime::manual_ocr_target_available(5U, true));
    require(infini::runtime::manual_ocr_target_available(6U, true));
    require(infini::runtime::manual_ocr_target_available(10U, true));
    require(!infini::runtime::manual_ocr_target_available(1U, false));
    require(!infini::runtime::manual_ocr_target_available(2U, true));
    require(!infini::runtime::manual_ocr_target_available(7U, true));
    require(!infini::runtime::manual_ocr_allows_signature(false, false));
    require(infini::runtime::manual_ocr_allows_signature(true, false));

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

    auto target_payload = valid_capture_target_command();
    const auto target = infini::runtime::parse_capture_target_command(target_payload);
    require(target.has_value());
    require(target->operation == infini::runtime::CaptureTargetOperation::upsert);
    require(target->kind == infini::runtime::CaptureTargetKind::window);
    require(target->command_revision == 7U);
    require(target->native_handle == 0x1234U);

    target_payload[6U] = std::byte{1};
    require(!infini::runtime::parse_capture_target_command(target_payload).has_value());
    target_payload = valid_capture_target_command();
    write_u64(target_payload, 48U, 0U);
    require(!infini::runtime::parse_capture_target_command(target_payload).has_value());
    target_payload = valid_capture_target_command();
    target_payload[5U] = std::byte{3};
    write_u64(target_payload, 48U, 0U);
    write_u32(target_payload, 64U, 16'385U);
    write_u32(target_payload, 68U, 1080U);
    require(!infini::runtime::parse_capture_target_command(target_payload).has_value());

    std::array<std::byte, 8U> manual_ocr_payload{};
    write_u32(manual_ocr_payload, 0U, 2U);
    manual_ocr_payload[4U] = std::byte{1};
    manual_ocr_payload[5U] = std::byte{1};
    const auto all_targets_manual_ocr =
        infini::runtime::parse_manual_ocr_request(manual_ocr_payload);
    require(all_targets_manual_ocr.has_value());
    require(all_targets_manual_ocr->scope ==
        infini::runtime::ManualOcrScope::all_targets);
    require(all_targets_manual_ocr->target_instance_ids.empty());

    std::vector<std::byte> explicit_manual_ocr(40U);
    write_u32(explicit_manual_ocr, 0U, 2U);
    explicit_manual_ocr[4U] = std::byte{1};
    explicit_manual_ocr[5U] = std::byte{2};
    write_u16(explicit_manual_ocr, 6U, 2U);
    explicit_manual_ocr[8U] = std::byte{1};
    explicit_manual_ocr[24U] = std::byte{2};
    const auto selected_targets_manual_ocr =
        infini::runtime::parse_manual_ocr_request(explicit_manual_ocr);
    require(selected_targets_manual_ocr.has_value());
    require(selected_targets_manual_ocr->scope ==
        infini::runtime::ManualOcrScope::explicit_targets);
    require(selected_targets_manual_ocr->target_instance_ids.size() == 2U);
    require(selected_targets_manual_ocr->target_instance_ids[0U][0U] == std::byte{1});
    require(selected_targets_manual_ocr->target_instance_ids[1U][0U] == std::byte{2});

    explicit_manual_ocr[24U] = std::byte{1};
    require(!infini::runtime::parse_manual_ocr_request(explicit_manual_ocr).has_value());
    explicit_manual_ocr[24U] = std::byte{2};
    explicit_manual_ocr[8U] = std::byte{};
    require(!infini::runtime::parse_manual_ocr_request(explicit_manual_ocr).has_value());
    explicit_manual_ocr[8U] = std::byte{1};
    explicit_manual_ocr.push_back(std::byte{});
    require(!infini::runtime::parse_manual_ocr_request(explicit_manual_ocr).has_value());

    std::array<std::byte, 24U> thumbnail_payload{};
    write_u32(thumbnail_payload, 0U, 1U);
    write_u32(thumbnail_payload, 4U, 960U);
    thumbnail_payload[8U] = std::byte{1};
    const auto thumbnail =
        infini::runtime::parse_thumbnail_request(thumbnail_payload);
    require(thumbnail.has_value());
    require(thumbnail->maximum_long_edge == 960U);
    require(thumbnail->target_instance_id.front() == std::byte{1});
    write_u32(thumbnail_payload, 4U, 319U);
    require(!infini::runtime::parse_thumbnail_request(
        thumbnail_payload).has_value());
    write_u32(thumbnail_payload, 4U, 1'281U);
    require(!infini::runtime::parse_thumbnail_request(
        thumbnail_payload).has_value());
    write_u32(thumbnail_payload, 4U, 960U);
    thumbnail_payload[8U] = std::byte{};
    require(!infini::runtime::parse_thumbnail_request(
        thumbnail_payload).has_value());

    auto overlay_payload = valid_overlay_command();
    const auto overlay = infini::runtime::parse_overlay_desired_state(overlay_payload);
    require(overlay.has_value());
    require(overlay->revision == 9U);
    require(overlay->regions.size() == 1U);
    require(overlay->regions.front().ordered_slots.size() == 1U);
    require(overlay->regions.front().destination_bounds.has_value());
    require(overlay->regions.front().destination_bounds->x == 1000.0F);
    require(overlay->regions.front().style.blur_radius == 14.0F);
    require(overlay->regions.front().style.outline_width == 1.5F);
    require(overlay->regions.front().style.outline_color.red == 1.0F / 255.0F);
    require(overlay->regions.front().style.minimum_dwell_milliseconds == 650U);
    require(overlay->regions.front().style.crossfade_milliseconds == 140U);
    require(overlay->regions.front().style.reduced_motion);
    require(overlay->regions.front().style.automatic_shrink);
    require(overlay->regions.front().style.no_scroll_overflow);
    require(overlay->regions.front().style.maximum_height == 180U);
    require(overlay->regions.front().ordered_slots.front().stage_index == 2U);
    require(overlay->regions.front().ordered_slots.front().text == u"translated");
    overlay_payload[82U] = std::byte{1};
    require(!infini::runtime::parse_overlay_desired_state(overlay_payload).has_value());

    auto policy_payload = valid_policy_revision();
    const auto policy = infini::runtime::parse_policy_revision(policy_payload);
    require(policy.has_value());
    require(policy->revision == 12U);
    require(policy->profile_id.front() == std::byte{6});
    require(policy->profile_revision == 4U);
    require(policy->regions.front().policy == "3:paused");
    policy_payload.push_back(std::byte{});
    require(!infini::runtime::parse_policy_revision(policy_payload).has_value());

    auto processing_payload = valid_processing_configuration();
    const auto processing = infini::runtime::parse_processing_configuration(
        processing_payload);
    require(processing.has_value());
    require(processing->configuration_revision == 9U);
    require(processing->profile_id.front() == std::byte{6});
    require(processing->profile_revision == 4U);
    require(processing->detection_long_edge == 1920U);
    require(processing->scan_remaining_area);
    require(processing->regions.size() == 1U);
    require(processing->regions.front().ocr_provider_id == "ocr.windows.media");
    require(processing->regions.front().recognition_language == "ja-JP");
    processing_payload[65U] = std::byte{1};
    require(!infini::runtime::parse_processing_configuration(
        processing_payload).has_value());

    auto ocr_payload = valid_ocr_result();
    const auto ocr = infini::runtime::parse_ocr_result(ocr_payload);
    require(ocr.has_value());
    require(ocr->token.source_generation == 5U);
    require(ocr->token.profile_revision == 6U);
    require(ocr->token.result_sequence == 8U);
    require(ocr->stable);
    require(ocr->lines.size() == 1U);
    require(ocr->lines.front().text == u"attack 100");
    require(ocr->lines.front().orientation_degrees == 90);
    require(ocr->lines.front().vertical);
    const auto reencoded_ocr = infini::runtime::encode_ocr_result(*ocr);
    require(reencoded_ocr.has_value());
    const auto reparsed_ocr = infini::runtime::parse_ocr_result(*reencoded_ocr);
    require(reparsed_ocr.has_value());
    require(reparsed_ocr->lines.front().text == u"attack 100");
    require(reparsed_ocr->lines.front().orientation_degrees == 90);
    require(reparsed_ocr->lines.front().vertical);
    infini::runtime::OcrResultCommand manual_ocr = *ocr;
    manual_ocr.token.manual = true;
    const auto encoded_manual_ocr = infini::runtime::encode_ocr_result(manual_ocr);
    require(encoded_manual_ocr.has_value());
    const auto reparsed_manual_ocr =
        infini::runtime::parse_ocr_result(*encoded_manual_ocr);
    require(reparsed_manual_ocr.has_value());
    require(reparsed_manual_ocr->token.manual);
    ocr_payload[121U] = std::byte{1};
    require(!infini::runtime::parse_ocr_result(ocr_payload).has_value());

    infini::runtime::CloudOcrCropEvent crop{};
    crop.token.runtime_epoch[0] = std::byte{1};
    crop.token.target_instance_id[0] = std::byte{2};
    crop.token.area_kind = 0U;
    crop.token.manual = true;
    crop.token.region_id[0] = std::byte{3};
    crop.token.text_track_id[0] = std::byte{4};
    crop.token.source_generation = 5U;
    crop.token.profile_revision = 6U;
    crop.token.ocr_run_id[0] = std::byte{7};
    crop.token.attempt = 1U;
    crop.token.result_sequence = 1U;
    crop.provider_id = "ocr.google-vision";
    crop.mime_type = "image/png";
    crop.encoded_crop = {std::byte{0x89}, std::byte{'P'}, std::byte{'N'}, std::byte{'G'}};
    crop.pixel_width = 320U;
    crop.pixel_height = 120U;
    crop.consent_policy_revision = 9U;
    crop.deadline_utc_ticks = 638'900'000'000'000'000ULL;
    crop.encoded_byte_ceiling = 1024U;
    const auto encoded_crop = infini::runtime::encode_cloud_ocr_crop_request(crop);
    require(encoded_crop.has_value());
    require(read_u32(*encoded_crop, 0U) == 1U);
    require((*encoded_crop)[37U] == std::byte{1});
    require(read_u64(*encoded_crop, 72U) == 5U);
    require(read_u64(*encoded_crop, 120U) == 9U);
    require(read_u32(*encoded_crop, 148U) == 4U);
    require(encoded_crop->size() == 160U + 9U + 17U + 4U);

    return EXIT_SUCCESS;
}
