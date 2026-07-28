#pragma once

#include <array>
#include <algorithm>
#include <cstddef>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

namespace infini::overlay {

using identity = std::array<std::byte, 16U>;

struct rect_f final {
    float x{};
    float y{};
    float width{};
    float height{};
};

struct point_f final {
    float x{};
    float y{};
};

enum class slot_state : std::uint8_t {
    waiting,
    streaming,
    success,
    fallback,
    timeout,
    failure,
    cancelled,
};

enum class background_mode : std::uint8_t {
    replacement,
    translucent,
    offset,
    floating_panel,
    opaque,
    temporal_cache,
    automatic_contrast,
    no_cover,
};

enum class text_alignment : std::uint8_t { left, center, right };

struct color_rgba final {
    float red{};
    float green{};
    float blue{};
    float alpha{1.0F};
};

struct background_frame final {
    ~background_frame() noexcept;

    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t stride{};
    std::vector<std::byte> pixels;
    color_rgba average{};
};

struct slot final {
    identity id{};
    std::uint32_t order{};
    slot_state state{};
    std::uint32_t stage_index{};
    std::u16string text;
    std::u16string label;
};

struct region_style final {
    background_mode background{background_mode::translucent};
    text_alignment alignment{text_alignment::left};
    color_rgba background_color{0.0F, 0.0F, 0.0F, 0.65F};
    color_rgba text_color{1.0F, 1.0F, 1.0F, 1.0F};
    color_rgba outline_color{0.0F, 0.0F, 0.0F, 1.0F};
    float padding{8.0F};
    float preferred_font_size{24.0F};
    float minimum_font_size{12.0F};
    float blur_radius{12.0F};
    float outline_width{1.0F};
    std::uint32_t maximum_lines{4};
    std::uint32_t maximum_height{};
    bool automatic_shrink{true};
    bool no_scroll_overflow{true};
    std::uint32_t minimum_dwell_milliseconds{500U};
    std::uint32_t crossfade_milliseconds{120U};
    bool reduced_motion{};
};

struct region final {
    identity id{};
    rect_f bounds{};
    region_style style{};
    std::optional<rect_f> destination_bounds{};
    std::shared_ptr<const background_frame> background_pixels;
    std::vector<slot> ordered_slots;
};

[[nodiscard]] inline rect_f display_bounds(const region& value) noexcept
{
    rect_f result = value.destination_bounds.value_or(value.bounds);
    if (value.style.maximum_height != 0U)
        result.height = (std::min)(result.height, static_cast<float>(value.style.maximum_height));
    return result;
}

struct desired_state final {
    identity runtime_epoch{};
    identity target_instance{};
    std::uint64_t revision{};
    std::vector<region> regions;
};

} // namespace infini::overlay
