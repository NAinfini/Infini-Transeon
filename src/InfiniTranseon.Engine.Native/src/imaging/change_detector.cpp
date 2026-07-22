#include <infini/imaging/change_detector.hpp>

#include <algorithm>
#include <cmath>
#include <limits>
#include <optional>
#include <stdexcept>

namespace infini::imaging
{
namespace
{
bool valid(const normalized_rect& rectangle) noexcept
{
    return std::isfinite(rectangle.x) && std::isfinite(rectangle.y) &&
        std::isfinite(rectangle.width) && std::isfinite(rectangle.height) &&
        rectangle.x >= 0.0 && rectangle.y >= 0.0 && rectangle.width > 0.0 &&
        rectangle.height > 0.0 && rectangle.x + rectangle.width <= 1.0 &&
        rectangle.y + rectangle.height <= 1.0;
}

std::optional<normalized_rect> intersection(
    const normalized_rect& left,
    const normalized_rect& right) noexcept
{
    const double x = std::max(left.x, right.x);
    const double y = std::max(left.y, right.y);
    const double edge_x = std::min(left.x + left.width, right.x + right.width);
    const double edge_y = std::min(left.y + left.height, right.y + right.height);
    if (edge_x <= x || edge_y <= y) return std::nullopt;
    return normalized_rect{x, y, edge_x - x, edge_y - y};
}

void add_fragment(
    std::vector<normalized_rect>& fragments,
    const double x,
    const double y,
    const double width,
    const double height)
{
    if (width > 0.0 && height > 0.0) fragments.push_back({x, y, width, height});
}
}

image_size detection_plane_size(const image_size source, const std::int32_t maximum_long_edge)
{
    if (source.width <= 0 || source.height <= 0 || maximum_long_edge <= 0)
        throw std::invalid_argument("image dimensions and maximum edge must be positive");
    const std::int32_t long_edge = std::max(source.width, source.height);
    if (long_edge <= maximum_long_edge) return source;
    const double scale = static_cast<double>(maximum_long_edge) / long_edge;
    return {
        std::max(1, static_cast<std::int32_t>(std::lround(source.width * scale))),
        std::max(1, static_cast<std::int32_t>(std::lround(source.height * scale)))};
}

std::vector<std::uint32_t> changed_tiles(
    const std::span<const std::uint64_t> previous,
    const std::span<const std::uint64_t> current,
    const std::uint64_t threshold)
{
    if (previous.size() != current.size())
        throw std::invalid_argument("change planes must contain the same number of tiles");
    if (previous.size() > std::numeric_limits<std::uint32_t>::max())
        throw std::length_error("change plane exceeds the supported tile count");
    std::vector<std::uint32_t> changed;
    for (std::size_t index = 0; index < current.size(); ++index)
    {
        const std::uint64_t delta = previous[index] >= current[index]
            ? previous[index] - current[index]
            : current[index] - previous[index];
        if (delta > threshold) changed.push_back(static_cast<std::uint32_t>(index));
    }
    return changed;
}

change_signature make_bgra_change_signature(
    const std::span<const std::uint8_t> pixels,
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t stride,
    const std::uint32_t maximum_columns,
    const std::uint32_t maximum_rows)
{
    constexpr std::uint32_t bytes_per_pixel = 4U;
    constexpr std::uint32_t samples_per_axis = 8U;
    constexpr std::uint32_t maximum_signature_cells = 65'536U;
    if (width == 0U || height == 0U || maximum_columns == 0U || maximum_rows == 0U ||
        maximum_columns > maximum_signature_cells || maximum_rows > maximum_signature_cells ||
        static_cast<std::uint64_t>(maximum_columns) * maximum_rows > maximum_signature_cells ||
        stride < static_cast<std::uint64_t>(width) * bytes_per_pixel ||
        pixels.size() < static_cast<std::uint64_t>(stride) * height)
    {
        throw std::invalid_argument("BGRA change-signature input is invalid");
    }

    change_signature signature{};
    signature.columns = (std::min)(width, maximum_columns);
    signature.rows = (std::min)(height, maximum_rows);
    signature.luminance.resize(
        static_cast<std::size_t>(signature.columns) * signature.rows);
    for (std::uint32_t row = 0U; row < signature.rows; ++row)
    {
        const std::uint32_t top = static_cast<std::uint32_t>(
            static_cast<std::uint64_t>(row) * height / signature.rows);
        const std::uint32_t bottom = static_cast<std::uint32_t>(
            static_cast<std::uint64_t>(row + 1U) * height / signature.rows);
        const std::uint32_t y_samples = (std::min)(samples_per_axis, bottom - top);
        for (std::uint32_t column = 0U; column < signature.columns; ++column)
        {
            const std::uint32_t left = static_cast<std::uint32_t>(
                static_cast<std::uint64_t>(column) * width / signature.columns);
            const std::uint32_t right = static_cast<std::uint32_t>(
                static_cast<std::uint64_t>(column + 1U) * width / signature.columns);
            const std::uint32_t x_samples = (std::min)(samples_per_axis, right - left);
            std::uint64_t sum{};
            for (std::uint32_t sample_y = 0U; sample_y < y_samples; ++sample_y)
            {
                const std::uint32_t y = top + static_cast<std::uint32_t>(
                    (static_cast<std::uint64_t>(sample_y) * 2U + 1U) *
                    (bottom - top) / (2U * y_samples));
                for (std::uint32_t sample_x = 0U; sample_x < x_samples; ++sample_x)
                {
                    const std::uint32_t x = left + static_cast<std::uint32_t>(
                        (static_cast<std::uint64_t>(sample_x) * 2U + 1U) *
                        (right - left) / (2U * x_samples));
                    const std::size_t offset = static_cast<std::size_t>(y) * stride +
                        static_cast<std::size_t>(x) * bytes_per_pixel;
                    const std::uint32_t luminance =
                        29U * pixels[offset] +
                        150U * pixels[offset + 1U] +
                        77U * pixels[offset + 2U];
                    sum += (luminance + 128U) >> 8U;
                }
            }
            const std::uint32_t sample_count = x_samples * y_samples;
            signature.luminance[
                static_cast<std::size_t>(row) * signature.columns + column] =
                static_cast<std::uint8_t>((sum + sample_count / 2U) / sample_count);
        }
    }
    return signature;
}

bool meaningfully_changed(
    const change_signature& previous,
    const change_signature& current,
    const std::uint8_t cell_delta_threshold,
    const std::uint32_t minimum_changed_cells)
{
    if (previous.columns == 0U || previous.rows == 0U ||
        previous.columns != current.columns || previous.rows != current.rows ||
        previous.luminance.size() != current.luminance.size() ||
        previous.luminance.size() !=
            static_cast<std::size_t>(previous.columns) * previous.rows ||
        minimum_changed_cells == 0U || minimum_changed_cells > previous.luminance.size())
    {
        throw std::invalid_argument("Change signatures are incompatible");
    }

    std::uint32_t changed{};
    for (std::size_t index = 0U; index < current.luminance.size(); ++index)
    {
        const std::uint8_t left = previous.luminance[index];
        const std::uint8_t right = current.luminance[index];
        const std::uint8_t delta = left >= right ? left - right : right - left;
        if (delta > cell_delta_threshold && ++changed >= minimum_changed_cells)
            return true;
    }
    return false;
}

remaining_mask compute_remaining_area_mask(
    const normalized_rect target,
    const std::span<const normalized_rect> exclusions,
    const std::size_t maximum_fragments)
{
    if (!valid(target) || maximum_fragments == 0U ||
        std::any_of(exclusions.begin(), exclusions.end(), [](const auto& item) { return !valid(item); }))
    {
        return {};
    }
    std::vector<normalized_rect> fragments{target};
    for (const normalized_rect& exclusion : exclusions)
    {
        std::vector<normalized_rect> next;
        for (const normalized_rect& fragment : fragments)
        {
            const std::optional<normalized_rect> overlap = intersection(fragment, exclusion);
            if (!overlap)
            {
                next.push_back(fragment);
                continue;
            }
            const double fragment_right = fragment.x + fragment.width;
            const double fragment_bottom = fragment.y + fragment.height;
            const double overlap_right = overlap->x + overlap->width;
            const double overlap_bottom = overlap->y + overlap->height;
            add_fragment(next, fragment.x, fragment.y, fragment.width, overlap->y - fragment.y);
            add_fragment(next, fragment.x, overlap_bottom, fragment.width, fragment_bottom - overlap_bottom);
            add_fragment(next, fragment.x, overlap->y, overlap->x - fragment.x, overlap->height);
            add_fragment(next, overlap_right, overlap->y, fragment_right - overlap_right, overlap->height);
            if (next.size() > maximum_fragments)
                return {remaining_mask_status::capacity_rejected, {}};
        }
        fragments = std::move(next);
    }
    return {remaining_mask_status::ok, std::move(fragments)};
}

std::vector<detection_candidate> exclude_explicit_region_overlaps(
    const std::span<const detection_candidate> candidates,
    const std::span<const normalized_rect> explicit_regions,
    const double minimum_candidate_overlap)
{
    if (!std::isfinite(minimum_candidate_overlap) || minimum_candidate_overlap < 0.0 ||
        minimum_candidate_overlap > 1.0)
    {
        throw std::invalid_argument("candidate overlap threshold must be within [0, 1]");
    }
    std::vector<detection_candidate> result;
    result.reserve(candidates.size());
    for (const detection_candidate& candidate : candidates)
    {
        if (!valid(candidate.bounds)) throw std::invalid_argument("detection candidate bounds are invalid");
        const double candidate_area = candidate.bounds.width * candidate.bounds.height;
        const bool overlaps = std::any_of(explicit_regions.begin(), explicit_regions.end(),
            [&](const normalized_rect& explicit_region)
            {
                if (!valid(explicit_region))
                    throw std::invalid_argument("explicit region bounds are invalid");
                const auto overlap = intersection(candidate.bounds, explicit_region);
                return overlap && overlap->width * overlap->height / candidate_area >= minimum_candidate_overlap;
            });
        if (!overlaps) result.push_back(candidate);
    }
    return result;
}
}
