#pragma once

#include "infini/overlay/overlay_types.hpp"

namespace infini::overlay {

struct slot_layout final {
    identity slot_id{};
    rect_f bounds{};
    float font_size{};
    std::uint32_t line_count{};
    bool overflow{};
};

[[nodiscard]] std::vector<slot_layout> layout_fixed_slots(
    const region& value,
    float pixels_per_dip = 1.0F);

} // namespace infini::overlay
