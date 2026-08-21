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

std::vector<line_layout> layout_slot_lines(
    const region& value,
    const slot& item,
    const float pixels_per_dip) {
    const rect_f display = display_bounds(value);
    if (item.lines.empty() || pixels_per_dip <= 0.0F ||
        value.bounds.width <= 0.0F || value.bounds.height <= 0.0F ||
        display.width <= 0.0F || display.height <= 0.0F) return {};

    // Line rectangles arrive in the coordinates of the text they replace. A region drawn away from
    // that text - an offset panel, or one clamped by maximum_height - moves the whole slot with it,
    // so every rectangle goes through the same mapping the region itself went through.
    const float scale_x = display.width / value.bounds.width;
    const float scale_y = display.height / value.bounds.height;
    const float padding = value.style.padding * pixels_per_dip;
    const float floor = value.style.minimum_font_size * pixels_per_dip;

    std::vector<line_layout> result;
    result.reserve(item.lines.size());
    for (const slot_line& line : item.lines) {
        const rect_f bounds{
            display.x + (line.bounds.x - value.bounds.x) * scale_x,
            display.y + (line.bounds.y - value.bounds.y) * scale_y,
            line.bounds.width * scale_x,
            line.bounds.height * scale_y};
        const float available_width = std::max(1.0F, bounds.width - 2.0F * padding);
        const float available_height = std::max(1.0F, bounds.height);
        // The replaced text filled its rectangle, so the translation takes its size from that
        // rectangle rather than from the profile's preferred size - that is what keeps the two
        // roughly the same size. A rectangle covering several captured lines divides its height
        // between them first; the trailing factor is the share of a line's box its glyphs occupy.
        const auto covered = static_cast<float>(std::max(1U, line.source_line_count));
        float font_size = std::max(floor, available_height / covered * 0.78F);
        std::uint32_t lines = estimate_lines(line.text, available_width, font_size);
        while (value.style.automatic_shrink && font_size > floor &&
               (lines > value.style.maximum_lines ||
                static_cast<float>(lines) * font_size * 1.25F > available_height)) {
            font_size = std::max(floor, font_size - pixels_per_dip);
            lines = estimate_lines(line.text, available_width, font_size);
        }
        result.push_back(line_layout{
            bounds,
            font_size,
            std::min(lines, value.style.maximum_lines),
            lines > value.style.maximum_lines ||
                static_cast<float>(lines) * font_size * 1.25F > available_height,
        });
    }
    return result;
}

} // namespace infini::overlay
