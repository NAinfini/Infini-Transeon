#include "infini/overlay/background_treatment.hpp"
#include "infini/overlay/overlay_renderer.hpp"
#include "infini/overlay/text_layout.hpp"
#include "infini/overlay/overlay_window.hpp"
#include "infini/diagnostics/performance_sampler.hpp"

#include <cassert>
#include <cmath>
#include <cstddef>
#include <memory>
#include <span>
#include <vector>

int main() {
    using namespace infini::overlay;
    assert(should_show_overlay({true, false, false, true}));
    assert(!should_show_overlay({false, false, false, true}));
    assert(!should_show_overlay({true, true, false, true}));
    assert(!should_show_overlay({true, false, true, true}));
    assert(!should_show_overlay({true, false, false, false}));
    const auto identity_value = [](const std::byte value) {
        identity result{};
        result.front() = value;
        return result;
    };
    region value{};
    value.id = identity_value(std::byte{1});
    value.bounds = {0.0F, 0.0F, 800.0F, 320.0F};
    value.style.maximum_lines = 3U;
    value.ordered_slots = {
        slot{identity_value(std::byte{2}), 1U, slot_state::waiting, 0U, u"", u"B"},
        slot{identity_value(std::byte{1}), 0U, slot_state::success, 1U, u"A long translated subtitle that must wrap without moving other slots", u"A"},
        slot{identity_value(std::byte{3}), 2U, slot_state::failure, 0U, u"", u"C"},
        slot{identity_value(std::byte{4}), 3U, slot_state::streaming, 1U, u"实时翻译结果", u"D"},
    };
    const auto layout = layout_fixed_slots(value, 3.0F);
    assert(layout.size() == 4U);
    assert(layout[0].slot_id == identity_value(std::byte{1}));
    assert(std::abs(layout[0].bounds.height - 80.0F) < 0.01F);
    assert(layout[1].bounds.y == 80.0F);
    assert(layout[0].font_size >= value.style.minimum_font_size * 3.0F);

    value.style.background = background_mode::offset;
    value.destination_bounds = rect_f{1000.0F, 100.0F, 600.0F, 300.0F};
    const auto offset_layout = layout_fixed_slots(value, 1.0F);
    assert(offset_layout.front().bounds.x == 1000.0F);
    assert(offset_layout.front().bounds.y == 100.0F);
    assert(offset_layout.front().bounds.width == 600.0F);

    value.style.background = background_mode::floating_panel;
    desired_state hover_state{
        identity_value(std::byte{1}), identity_value(std::byte{2}), 1U, {value}};
    const auto hidden_panel = visible_state_for_cursor(
        hover_state, point_f{900.0F, 100.0F});
    const auto visible_panel = visible_state_for_cursor(
        hover_state, point_f{100.0F, 100.0F});
    const auto unavailable_cursor = visible_state_for_cursor(hover_state, std::nullopt);
    assert(hidden_panel.regions.empty());
    assert(visible_panel.regions.size() == 1U);
    assert(unavailable_cursor.regions.empty());

    value.style.background = background_mode::automatic_contrast;
    value.destination_bounds.reset();
    const auto light = resolve_background(value.style, {0.9F, 0.9F, 0.9F, 1.0F}, false);
    const auto dark = resolve_background(value.style, {0.05F, 0.05F, 0.05F, 1.0F}, false);
    assert(light.text.red == 0.0F);
    assert(dark.text.red == 1.0F);

    const std::vector<std::byte> pixels{
        std::byte{255}, std::byte{255}, std::byte{255}, std::byte{255},
        std::byte{0}, std::byte{0}, std::byte{0}, std::byte{255},
    };
    const auto background = make_background_frame(pixels, 2U, 1U, 8U, 64U);
    assert(background);
    assert(std::abs(background->average.red - 0.5F) < 0.01F);
    assert(std::abs(background->average.green - 0.5F) < 0.01F);
    assert(std::abs(background->average.blue - 0.5F) < 0.01F);
    assert(!make_background_frame(pixels, 2U, 1U, 7U, 64U));
    const auto cropped = crop_background_frame(
        *background, {0.0F, 0.0F, 1.0F, 1.0F}, {0.0F, 0.0F, 2.0F, 1.0F}, 64U);
    assert(cropped && cropped->width == 1U && cropped->height == 1U);
    assert(cropped->average.red > 0.99F);
    const auto blurred = blur_background_frame(*background, 1U, 64U);
    assert(blurred && blurred->pixels != background->pixels);
    background_blur_cache blur_cache;
    const auto first_blur = blur_cache.get(background, 1U, 64U);
    const auto reused_blur = blur_cache.get(background, 1U, 64U);
    assert(first_blur && first_blur == reused_blur);
    blur_cache.clear();
    assert(blur_cache.get(background, 1U, 64U) != first_blur);

    background_frame_cache cache{64U};
    assert(cache.update(identity_value(std::byte{9}), background, false));
    assert(cache.snapshot(identity_value(std::byte{9}), false) == background);
    assert(!cache.snapshot(identity_value(std::byte{9}), true));
    assert(cache.update(identity_value(std::byte{9}), background, true));
    assert(cache.snapshot(identity_value(std::byte{9}), true) == background);
    assert(cache.retained_bytes() == pixels.size());
    cache.clear();
    assert(cache.retained_bytes() == 0U);
    assert(!cache.snapshot(identity_value(std::byte{9}), false));

    renderer_state renderer;
    desired_state state{
        identity_value(std::byte{1}), identity_value(std::byte{2}), 1U, {value}};
    assert(renderer.apply(state, false) == apply_error::capture_exclusion_required);
    assert(renderer.apply(state, true) == apply_error::background_unavailable);
    state.regions.front().background_pixels = background;
    assert(renderer.apply(state, true) == apply_error::none);
    assert(renderer.applied_revision() == 1U);
    assert(renderer.apply(state, true) == apply_error::stale_revision);
    state.revision = 2U;
    assert(renderer.apply(state, true) == apply_error::none);

    desired_state refined = state;
    refined.revision = 3U;
    refined.regions.front().style.minimum_dwell_milliseconds = 650U;
    refined.regions.front().style.crossfade_milliseconds = 140U;
    refined.regions.front().ordered_slots.front().stage_index = 2U;
    refined.regions.front().ordered_slots.front().text = u"Refined translation";
    const auto transition = plan_refinement_transition(state, refined);
    assert(transition.has_value());
    assert(transition->minimum_dwell_milliseconds == 650U);
    assert(transition->crossfade_milliseconds == 140U);
    refined.regions.front().style.reduced_motion = true;
    assert(plan_refinement_transition(state, refined)->crossfade_milliseconds == 0U);
    refined.regions.front().ordered_slots.front().stage_index =
        state.regions.front().ordered_slots.front().stage_index;
    assert(!plan_refinement_transition(state, refined).has_value());

    infini::diagnostics::performance_sampler sampler{3U};
    sampler.push({10.0, 100U, std::nullopt, 20.0, 100.0, 60.0, 1U});
    sampler.push({30.0, 200U, 4.0, 40.0, 200.0, 30.0, 2U});
    const auto summary = sampler.summarize();
    assert(summary.sample_count == 2U);
    assert(summary.committed_bytes_peak == 200U);
    assert(summary.gpu_time_p95.has_value());
    assert(summary.queue_replacements_total == 3U);
    return 0;
}
