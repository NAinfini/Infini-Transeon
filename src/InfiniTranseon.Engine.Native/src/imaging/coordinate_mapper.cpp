#include <infini/imaging/coordinate_mapper.hpp>

#include <algorithm>
#include <cmath>
#include <cstdint>
#include <limits>

namespace infini::imaging
{
namespace
{
bool valid(const normalized_rect& region) noexcept
{
    return std::isfinite(region.x) && std::isfinite(region.y) &&
        std::isfinite(region.width) && std::isfinite(region.height) &&
        region.x >= 0.0 && region.y >= 0.0 && region.width > 0.0 &&
        region.height > 0.0 && region.x + region.width <= 1.0 &&
        region.y + region.height <= 1.0;
}

normalized_rect rotate(const normalized_rect& region, const display_rotation rotation) noexcept
{
    switch (rotation)
    {
    case display_rotation::identity:
        return region;
    case display_rotation::clockwise_90:
        return {1.0 - region.y - region.height, region.x, region.height, region.width};
    case display_rotation::clockwise_180:
        return {
            1.0 - region.x - region.width,
            1.0 - region.y - region.height,
            region.width,
            region.height};
    case display_rotation::clockwise_270:
        return {region.y, 1.0 - region.x - region.width, region.height, region.width};
    }
    return region;
}

std::int32_t lower_edge(const double value, const std::int32_t extent) noexcept
{
    return std::clamp(
        static_cast<std::int32_t>(std::floor(value * static_cast<double>(extent))),
        0,
        extent);
}

std::int32_t upper_edge(const double value, const std::int32_t extent) noexcept
{
    return std::clamp(
        static_cast<std::int32_t>(std::ceil(value * static_cast<double>(extent))),
        0,
        extent);
}

bool can_add(const std::int32_t left, const std::int32_t right) noexcept
{
    const auto sum = static_cast<std::int64_t>(left) + right;
    return sum >= std::numeric_limits<std::int32_t>::min() &&
        sum <= std::numeric_limits<std::int32_t>::max();
}
}

mapped_region map_region(
    const normalized_rect region,
    const content_space content,
    const display_rotation rotation,
    const pixel_size minimum_size) noexcept
{
    if (!valid(region) || content.physical_size.width <= 0 ||
        content.physical_size.height <= 0 || content.dpi == 0U ||
        minimum_size.width <= 0 || minimum_size.height <= 0)
    {
        return {};
    }

    const normalized_rect transformed = rotate(region, rotation);
    const std::int32_t left = lower_edge(transformed.x, content.physical_size.width);
    const std::int32_t top = lower_edge(transformed.y, content.physical_size.height);
    const std::int32_t right = upper_edge(
        transformed.x + transformed.width,
        content.physical_size.width);
    const std::int32_t bottom = upper_edge(
        transformed.y + transformed.height,
        content.physical_size.height);
    const pixel_rect source{left, top, right - left, bottom - top};
    if (!can_add(content.desktop_origin.x, source.x) ||
        !can_add(content.desktop_origin.y, source.y))
    {
        return {};
    }

    const pixel_rect overlay{
        content.desktop_origin.x + source.x,
        content.desktop_origin.y + source.y,
        source.width,
        source.height};
    const mapping_status status = source.width < minimum_size.width ||
        source.height < minimum_size.height
        ? mapping_status::below_minimum_size
        : mapping_status::ok;
    return {status, source, overlay};
}
}
