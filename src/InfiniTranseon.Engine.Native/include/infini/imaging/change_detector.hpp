#pragma once

#include <cstdint>
#include <span>
#include <vector>

#include <infini/imaging/coordinate_mapper.hpp>

namespace infini::imaging
{
struct image_size
{
    std::int32_t width{};
    std::int32_t height{};
    bool operator==(const image_size&) const = default;
};

struct detection_candidate
{
    std::uint64_t candidate_id{};
    normalized_rect bounds{};
    std::uint64_t detection_epoch{};
};

enum class remaining_mask_status
{
    ok,
    capacity_rejected,
    invalid_input,
};

struct remaining_mask
{
    remaining_mask_status status{remaining_mask_status::invalid_input};
    std::vector<normalized_rect> fragments{};
};

struct change_signature
{
    std::uint32_t columns{};
    std::uint32_t rows{};
    std::vector<std::uint8_t> luminance{};
};

[[nodiscard]] image_size detection_plane_size(image_size source, std::int32_t maximum_long_edge = 1920);

[[nodiscard]] std::vector<std::uint32_t> changed_tiles(
    std::span<const std::uint64_t> previous,
    std::span<const std::uint64_t> current,
    std::uint64_t threshold);

[[nodiscard]] change_signature make_bgra_change_signature(
    std::span<const std::uint8_t> pixels,
    std::uint32_t width,
    std::uint32_t height,
    std::uint32_t stride,
    std::uint32_t maximum_columns = 64U,
    std::uint32_t maximum_rows = 36U);

[[nodiscard]] bool meaningfully_changed(
    const change_signature& previous,
    const change_signature& current,
    std::uint8_t cell_delta_threshold = 8U,
    std::uint32_t minimum_changed_cells = 1U);

[[nodiscard]] remaining_mask compute_remaining_area_mask(
    normalized_rect target,
    std::span<const normalized_rect> exclusions,
    std::size_t maximum_fragments);

[[nodiscard]] std::vector<detection_candidate> exclude_explicit_region_overlaps(
    std::span<const detection_candidate> candidates,
    std::span<const normalized_rect> explicit_regions,
    double minimum_candidate_overlap);
}
