#pragma once

#include "infini/overlay/overlay_types.hpp"

#include <optional>
#include <string_view>

namespace infini::overlay {

enum class apply_error : std::uint8_t {
    none,
    invalid_state,
    stale_revision,
    capture_exclusion_required,
    background_unavailable,
};

class renderer_state final {
public:
    [[nodiscard]] apply_error apply(
        const desired_state& state,
        bool capture_exclusion_active) noexcept;
    [[nodiscard]] std::uint64_t applied_revision() const noexcept;
    [[nodiscard]] const desired_state* current() const noexcept;

private:
    std::optional<desired_state> current_;
};

struct refinement_transition final {
    std::uint32_t minimum_dwell_milliseconds{};
    std::uint32_t crossfade_milliseconds{};
};

[[nodiscard]] std::optional<refinement_transition> plan_refinement_transition(
    const desired_state& previous,
    const desired_state& next) noexcept;

[[nodiscard]] desired_state visible_state_for_cursor(
    const desired_state& state,
    std::optional<point_f> cursor);

} // namespace infini::overlay
