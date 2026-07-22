#include "infini/overlay/overlay_renderer.hpp"

#include <algorithm>

namespace infini::overlay {
namespace {
bool nonzero(const identity& value) noexcept {
    return std::ranges::any_of(value, [](const std::byte item) {
        return item != std::byte{};
    });
}
}

apply_error renderer_state::apply(
    const desired_state& state,
    const bool capture_exclusion_active) noexcept {
    if (!capture_exclusion_active) return apply_error::capture_exclusion_required;
    if (!nonzero(state.runtime_epoch) || !nonzero(state.target_instance) || state.revision == 0U ||
        state.regions.size() > 256U) return apply_error::invalid_state;
    if (current_.has_value() &&
        (state.runtime_epoch != current_->runtime_epoch ||
         state.target_instance != current_->target_instance)) {
        current_.reset();
    }
    if (current_.has_value() && state.revision <= current_->revision)
        return apply_error::stale_revision;
    for (const auto& region : state.regions) {
        const bool destination_required = region.style.background == background_mode::offset ||
            region.style.background == background_mode::floating_panel;
        const bool captured_background_required =
            region.style.background == background_mode::temporal_cache ||
            region.style.background == background_mode::automatic_contrast ||
            (region.style.background == background_mode::translucent &&
                region.style.blur_radius > 0.0F);
        if (!nonzero(region.id) || region.ordered_slots.size() > 4U ||
            region.bounds.width <= 0.0F || region.bounds.height <= 0.0F ||
            destination_required != region.destination_bounds.has_value() ||
            (region.destination_bounds.has_value() &&
                (region.destination_bounds->width <= 0.0F ||
                    region.destination_bounds->height <= 0.0F)) ||
            region.style.minimum_font_size <= 0.0F ||
            region.style.preferred_font_size < region.style.minimum_font_size ||
            region.style.maximum_lines == 0U || region.style.blur_radius < 0.0F ||
            region.style.outline_width < 0.0F || region.style.outline_width > 8.0F ||
            region.style.minimum_dwell_milliseconds > 3'000U ||
            region.style.crossfade_milliseconds > 500U)
            return apply_error::invalid_state;
        if (captured_background_required && !region.background_pixels)
            return apply_error::background_unavailable;
    }
    current_ = state;
    return apply_error::none;
}

std::uint64_t renderer_state::applied_revision() const noexcept {
    return current_.has_value() ? current_->revision : 0U;
}

const desired_state* renderer_state::current() const noexcept {
    return current_.has_value() ? &*current_ : nullptr;
}

std::optional<refinement_transition> plan_refinement_transition(
    const desired_state& previous,
    const desired_state& next) noexcept {
    refinement_transition result{};
    bool found = false;
    for (const region& next_region : next.regions) {
        const auto previous_region = std::ranges::find_if(
            previous.regions,
            [&next_region](const region& value) { return value.id == next_region.id; });
        if (previous_region == previous.regions.end()) continue;
        const bool refined = std::ranges::any_of(
            next_region.ordered_slots,
            [&previous_region](const slot& next_slot) {
                const auto previous_slot = std::ranges::find_if(
                    previous_region->ordered_slots,
                    [&next_slot](const slot& value) { return value.id == next_slot.id; });
                return previous_slot != previous_region->ordered_slots.end() &&
                    next_slot.stage_index > previous_slot->stage_index &&
                    next_slot.text != previous_slot->text;
            });
        if (!refined) continue;
        found = true;
        result.minimum_dwell_milliseconds = (std::max)(
            result.minimum_dwell_milliseconds,
            next_region.style.minimum_dwell_milliseconds);
        result.crossfade_milliseconds = (std::max)(
            result.crossfade_milliseconds,
            next_region.style.reduced_motion
                ? 0U
                : next_region.style.crossfade_milliseconds);
    }
    return found ? std::optional<refinement_transition>{result} : std::nullopt;
}

desired_state visible_state_for_cursor(
    const desired_state& state,
    const std::optional<point_f> cursor) {
    desired_state visible = state;
    std::erase_if(visible.regions, [&](const region& value) {
        if (value.style.background != background_mode::floating_panel) return false;
        if (!cursor.has_value()) return true;
        return cursor->x < value.bounds.x || cursor->y < value.bounds.y ||
            cursor->x >= value.bounds.x + value.bounds.width ||
            cursor->y >= value.bounds.y + value.bounds.height;
    });
    return visible;
}

} // namespace infini::overlay
