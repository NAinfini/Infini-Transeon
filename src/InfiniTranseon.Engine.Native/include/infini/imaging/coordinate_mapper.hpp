#pragma once

#include <cstdint>

namespace infini::imaging
{
struct normalized_rect
{
    double x{};
    double y{};
    double width{};
    double height{};
};

struct pixel_point
{
    std::int32_t x{};
    std::int32_t y{};
    bool operator==(const pixel_point&) const = default;
};

struct pixel_size
{
    std::int32_t width{};
    std::int32_t height{};
    bool operator==(const pixel_size&) const = default;
};

struct pixel_rect
{
    std::int32_t x{};
    std::int32_t y{};
    std::int32_t width{};
    std::int32_t height{};
    bool operator==(const pixel_rect&) const = default;
};

struct content_space
{
    pixel_point desktop_origin{};
    pixel_size physical_size{};
    std::uint32_t dpi{};
};

enum class display_rotation
{
    identity,
    clockwise_90,
    clockwise_180,
    clockwise_270,
};

enum class mapping_status
{
    ok,
    below_minimum_size,
    invalid_input,
};

struct mapped_region
{
    mapping_status status{mapping_status::invalid_input};
    pixel_rect source{};
    pixel_rect overlay{};
};

[[nodiscard]] mapped_region map_region(
    normalized_rect region,
    content_space content,
    display_rotation rotation,
    pixel_size minimum_size) noexcept;
}
