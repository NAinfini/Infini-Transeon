#include "infini/overlay/text_layout.hpp"

#include <algorithm>
#include <cmath>

namespace infini::overlay {
namespace {

[[nodiscard]] std::uint32_t estimate_lines(
    const std::u16string& text,
    const float width,
    const float font_size) noexcept {
    if (text.empty()) return 1U;
    const auto characters_per_line = static_cast<std::uint32_t>(std::max(
        1.0F,
        std::floor(width / std::max(1.0F, font_size * 0.58F))));
    std::uint32_t lines = 1U;
    std::uint32_t column = 0U;
    for (const char16_t character : text) {
        if (character == u'\n') {
            ++lines;
            column = 0U;
        } else if (++column > characters_per_line) {
            ++lines;
            column = 1U;
        }
    }
    return lines;
}

} // namespace

std::vector<slot_layout> layout_fixed_slots(const region& value, const float pixels_per_dip) {
    const rect_f region_bounds = display_bounds(value);
    if (value.ordered_slots.empty() || value.ordered_slots.size() > 4U ||
        region_bounds.width <= 0.0F || region_bounds.height <= 0.0F ||
        pixels_per_dip <= 0.0F) return {};

    std::vector<const slot*> ordered;
    ordered.reserve(value.ordered_slots.size());
    for (const auto& item : value.ordered_slots) ordered.push_back(&item);
    std::ranges::sort(ordered, {}, &slot::order);
    const float slot_height = region_bounds.height / static_cast<float>(ordered.size());
    std::vector<slot_layout> result;
    result.reserve(ordered.size());
    for (std::size_t index = 0; index < ordered.size(); ++index) {
        const auto& item = *ordered[index];
        const float padding = value.style.padding * pixels_per_dip;
        const float available_width = std::max(1.0F, region_bounds.width - 2.0F * padding);
        const float available_height = std::max(1.0F, slot_height - 2.0F * padding);
        float font_size = value.style.preferred_font_size * pixels_per_dip;
        const float floor = value.style.minimum_font_size * pixels_per_dip;
        std::uint32_t lines = estimate_lines(item.text, available_width, font_size);
        while (value.style.automatic_shrink && font_size > floor &&
               (lines > value.style.maximum_lines || lines * font_size * 1.25F > available_height)) {
            font_size = std::max(floor, font_size - pixels_per_dip);
            lines = estimate_lines(item.text, available_width, font_size);
        }
        const bool overflow = lines > value.style.maximum_lines ||
            lines * font_size * 1.25F > available_height;
        result.push_back(slot_layout{
            item.id,
            rect_f{region_bounds.x, region_bounds.y + slot_height * static_cast<float>(index),
                   region_bounds.width, slot_height},
            font_size,
            std::min(lines, value.style.maximum_lines),
            overflow,
        });
    }
    return result;
}

} // namespace infini::overlay
