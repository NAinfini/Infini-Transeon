#pragma once

#include <cstddef>
#include <cstdint>
#include <string>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace infini::capture {

struct pixel_rect final {
    std::int32_t left{};
    std::int32_t top{};
    std::int32_t right{};
    std::int32_t bottom{};
    friend bool operator==(const pixel_rect&, const pixel_rect&) = default;
};

struct monitor_geometry final {
    std::uint64_t monitor_key{};
    pixel_rect desktop_pixels{};
    std::uint64_t adapter_key{};
};

struct desktop_region_piece final {
    std::uint64_t monitor_key{};
    std::uint64_t adapter_key{};
    pixel_rect monitor_pixels{};
    pixel_rect source_pixels{};
};

[[nodiscard]] std::vector<desktop_region_piece> split_desktop_region(
    pixel_rect desktop_region,
    const std::vector<monitor_geometry>& monitors);

[[nodiscard]] pixel_rect map_normalized_region_to_source(
    pixel_rect target_source_pixels,
    double x,
    double y,
    double width,
    double height);

enum class physical_source_kind : std::uint8_t { window, monitor };

struct physical_source_identity final {
    physical_source_kind kind{};
    std::uint64_t native_key{};
    std::uint64_t adapter_key{};
    friend bool operator==(const physical_source_identity&, const physical_source_identity&) = default;
};

struct physical_source_identity_hash final {
    [[nodiscard]] std::size_t operator()(const physical_source_identity& value) const noexcept;
};

[[nodiscard]] constexpr std::uint64_t pack_adapter_luid(
    const std::int32_t high_part,
    const std::uint32_t low_part) noexcept {
    return (static_cast<std::uint64_t>(static_cast<std::uint32_t>(high_part)) << 32U) |
        static_cast<std::uint64_t>(low_part);
}

class capture_source_registry final {
public:
    [[nodiscard]] bool attach(std::uint64_t logical_target_id, physical_source_identity source);
    [[nodiscard]] bool detach(std::uint64_t logical_target_id);
    [[nodiscard]] std::size_t source_count() const noexcept;
    [[nodiscard]] std::size_t logical_target_count(physical_source_identity source) const noexcept;

private:
    std::unordered_map<std::uint64_t, physical_source_identity> target_sources_;
    std::unordered_map<physical_source_identity, std::unordered_set<std::uint64_t>,
        physical_source_identity_hash> source_targets_;
};

struct window_binding final {
    std::wstring executable_name;
    std::wstring window_class;
};

struct window_observation final {
    std::uint64_t window_key{};
    std::wstring executable_name;
    std::wstring window_class;
    bool visible{};
    bool minimized{};
    bool cloaked{};
    bool on_current_virtual_desktop{};
};

enum class tracked_target_lifecycle : std::uint8_t {
    waiting_for_match,
    available,
    minimized,
    hidden,
    closed,
};

struct tracked_target_state final {
    std::uint64_t window_key{};
    tracked_target_lifecycle lifecycle{tracked_target_lifecycle::waiting_for_match};
};

class target_tracker final {
public:
    explicit target_tracker(window_binding binding);
    void observe(const window_observation& observation);
    void closed(std::uint64_t window_key) noexcept;
    [[nodiscard]] tracked_target_state state() const noexcept;

private:
    [[nodiscard]] bool matches(const window_observation& observation) const noexcept;
    window_binding binding_;
    tracked_target_state state_;
};

} // namespace infini::capture
