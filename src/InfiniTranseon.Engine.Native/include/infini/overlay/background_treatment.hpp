#pragma once

#include "infini/overlay/overlay_types.hpp"

#include <cstddef>
#include <cstdint>
#include <map>
#include <memory>
#include <mutex>
#include <span>

namespace infini::overlay {

struct background_result final {
    color_rgba background{};
    color_rgba text{};
    bool uses_cached_pixels{};
    bool covers_source{};
};

[[nodiscard]] background_result resolve_background(
    const region_style& style,
    color_rgba sampled_background,
    bool temporal_cache_available) noexcept;

[[nodiscard]] std::shared_ptr<const background_frame> make_background_frame(
    std::span<const std::byte> bgra_pixels,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride,
    std::size_t maximum_bytes) noexcept;

[[nodiscard]] std::shared_ptr<const background_frame> crop_background_frame(
    const background_frame& source,
    rect_f crop,
    rect_f source_extent,
    std::size_t maximum_bytes) noexcept;

[[nodiscard]] std::shared_ptr<const background_frame> blur_background_frame(
    const background_frame& source,
    std::uint32_t radius,
    std::size_t maximum_bytes) noexcept;

class background_frame_cache final {
public:
    explicit background_frame_cache(std::size_t maximum_bytes);

    [[nodiscard]] bool update(
        const identity& key,
        std::shared_ptr<const background_frame> frame,
        bool confirmed_clean) noexcept;
    [[nodiscard]] std::shared_ptr<const background_frame> snapshot(
        const identity& key,
        bool require_clean) noexcept;
    [[nodiscard]] std::size_t retained_bytes() const noexcept;
    void clear() noexcept;

private:
    struct entry final {
        std::shared_ptr<const background_frame> latest;
        std::shared_ptr<const background_frame> clean;
        std::uint64_t access{};
    };

    void trim(const identity& protected_key) noexcept;

    std::size_t maximum_bytes_{};
    std::uint64_t access_{};
    std::map<identity, entry> entries_;
};

class background_blur_cache final {
public:
    [[nodiscard]] std::shared_ptr<const background_frame> get(
        const std::shared_ptr<const background_frame>& source,
        std::uint32_t radius,
        std::size_t maximum_bytes) noexcept;
    void clear() noexcept;

private:
    struct entry final {
        std::weak_ptr<const background_frame> source;
        std::weak_ptr<const background_frame> blurred;
    };

    std::mutex gate_;
    std::map<std::pair<const background_frame*, std::uint32_t>, entry> entries_;
};

} // namespace infini::overlay
