#include "infini/capture/capture_targets.hpp"

#include <algorithm>
#include <cmath>
#include <cwctype>
#include <stdexcept>

namespace infini::capture {
namespace {

[[nodiscard]] bool valid(const pixel_rect value) noexcept {
    return value.right > value.left && value.bottom > value.top;
}

[[nodiscard]] bool equal_case_insensitive(const std::wstring& left, const std::wstring& right) noexcept {
    return left.size() == right.size() && std::equal(left.begin(), left.end(), right.begin(),
        [](const wchar_t a, const wchar_t b) { return std::towlower(a) == std::towlower(b); });
}

} // namespace

std::vector<desktop_region_piece> split_desktop_region(
    const pixel_rect desktop_region,
    const std::vector<monitor_geometry>& monitors) {
    if (!valid(desktop_region)) throw std::invalid_argument("desktop region is empty");
    std::vector<desktop_region_piece> result;
    result.reserve(monitors.size());
    for (const auto& monitor : monitors) {
        if (monitor.monitor_key == 0U || monitor.adapter_key == 0U || !valid(monitor.desktop_pixels))
            throw std::invalid_argument("invalid monitor geometry");
        const pixel_rect intersection{
            std::max(desktop_region.left, monitor.desktop_pixels.left),
            std::max(desktop_region.top, monitor.desktop_pixels.top),
            std::min(desktop_region.right, monitor.desktop_pixels.right),
            std::min(desktop_region.bottom, monitor.desktop_pixels.bottom),
        };
        if (!valid(intersection)) continue;
        result.push_back({
            monitor.monitor_key,
            monitor.adapter_key,
            intersection,
            {
                intersection.left - monitor.desktop_pixels.left,
                intersection.top - monitor.desktop_pixels.top,
                intersection.right - monitor.desktop_pixels.left,
                intersection.bottom - monitor.desktop_pixels.top,
            },
        });
    }
    return result;
}

pixel_rect map_normalized_region_to_source(
    const pixel_rect target_source_pixels,
    const double x,
    const double y,
    const double width,
    const double height)
{
    if (!valid(target_source_pixels) || !std::isfinite(x) || !std::isfinite(y) ||
        !std::isfinite(width) || !std::isfinite(height) || x < 0.0 || y < 0.0 ||
        width <= 0.0 || height <= 0.0 || x + width > 1.0 || y + height > 1.0)
        throw std::invalid_argument("normalized source region is invalid");
    const std::int32_t target_width = target_source_pixels.right - target_source_pixels.left;
    const std::int32_t target_height = target_source_pixels.bottom - target_source_pixels.top;
    const std::int32_t left = target_source_pixels.left + static_cast<std::int32_t>(
        std::floor(x * target_width));
    const std::int32_t top = target_source_pixels.top + static_cast<std::int32_t>(
        std::floor(y * target_height));
    const std::int32_t right = (std::clamp)(
        target_source_pixels.left + static_cast<std::int32_t>(
            std::ceil((x + width) * target_width)),
        left + 1,
        target_source_pixels.right);
    const std::int32_t bottom = (std::clamp)(
        target_source_pixels.top + static_cast<std::int32_t>(
            std::ceil((y + height) * target_height)),
        top + 1,
        target_source_pixels.bottom);
    return {left, top, right, bottom};
}

std::size_t physical_source_identity_hash::operator()(const physical_source_identity& value) const noexcept {
    const auto first = std::hash<std::uint64_t>{}(value.native_key);
    const auto second = std::hash<std::uint64_t>{}(value.adapter_key);
    return first ^ (second << 1U) ^ static_cast<std::size_t>(value.kind);
}

bool capture_source_registry::attach(
    const std::uint64_t logical_target_id,
    const physical_source_identity source) {
    if (logical_target_id == 0U || source.native_key == 0U || source.adapter_key == 0U) return false;
    const auto existing = target_sources_.find(logical_target_id);
    if (existing != target_sources_.end()) return existing->second == source;
    target_sources_.emplace(logical_target_id, source);
    source_targets_[source].insert(logical_target_id);
    return true;
}

bool capture_source_registry::detach(const std::uint64_t logical_target_id) {
    const auto existing = target_sources_.find(logical_target_id);
    if (existing == target_sources_.end()) return false;
    const auto source = existing->second;
    target_sources_.erase(existing);
    const auto source_entry = source_targets_.find(source);
    if (source_entry != source_targets_.end()) {
        source_entry->second.erase(logical_target_id);
        if (source_entry->second.empty()) source_targets_.erase(source_entry);
    }
    return true;
}

std::size_t capture_source_registry::source_count() const noexcept { return source_targets_.size(); }

std::size_t capture_source_registry::logical_target_count(
    const physical_source_identity source) const noexcept {
    const auto existing = source_targets_.find(source);
    return existing == source_targets_.end() ? 0U : existing->second.size();
}

target_tracker::target_tracker(window_binding binding) : binding_(std::move(binding)) {
    if (binding_.executable_name.empty() || binding_.window_class.empty())
        throw std::invalid_argument("target binding is incomplete");
}

void target_tracker::observe(const window_observation& observation) {
    if (observation.window_key == 0U || !matches(observation)) return;
    state_.window_key = observation.window_key;
    if (observation.minimized) state_.lifecycle = tracked_target_lifecycle::minimized;
    else if (!observation.visible || observation.cloaked || !observation.on_current_virtual_desktop)
        state_.lifecycle = tracked_target_lifecycle::hidden;
    else state_.lifecycle = tracked_target_lifecycle::available;
}

void target_tracker::closed(const std::uint64_t window_key) noexcept {
    if (state_.window_key != window_key) return;
    state_ = {};
}

tracked_target_state target_tracker::state() const noexcept { return state_; }

bool target_tracker::matches(const window_observation& observation) const noexcept {
    return equal_case_insensitive(binding_.executable_name, observation.executable_name) &&
        equal_case_insensitive(binding_.window_class, observation.window_class);
}

} // namespace infini::capture
