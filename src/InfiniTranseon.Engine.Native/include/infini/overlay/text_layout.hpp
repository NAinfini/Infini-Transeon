#pragma once

#include "infini/overlay/overlay_types.hpp"

namespace infini::overlay {

struct line_layout final {
    rect_f bounds{};
    float font_size{};
    std::uint32_t line_count{};
    bool overflow{};
};

// Returns one entry per slot line, in the slot's own order, empty when the region has no drawable
// geometry. The rectangles are in the coordinates the region is displayed at, not the ones its
// captured text occupies, so an offset or height-clamped region carries its lines along with it.
[[nodiscard]] std::vector<line_layout> layout_slot_lines(
    const region& value,
    const slot& item,
    float pixels_per_dip = 1.0F);

} // namespace infini::overlay
