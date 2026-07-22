#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace infini::ocr
{
struct bgra_image final
{
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t stride{};
    std::vector<std::byte> pixels;

    [[nodiscard]] bool valid() const noexcept;
};

struct normalized_mask_rect final
{
    double x{};
    double y{};
    double width{};
    double height{};
};

enum class cloud_crop_encode_status
{
    succeeded,
    invalid_image,
    com_unavailable,
    encoding_failed,
    capacity_rejected,
};

struct cloud_crop_encode_result final
{
    cloud_crop_encode_status status{cloud_crop_encode_status::invalid_image};
    std::vector<std::byte> bytes;
};

[[nodiscard]] bgra_image downscale_bgra(
    const bgra_image& source,
    std::uint32_t maximum_long_edge);

[[nodiscard]] bool mask_bgra_regions(
    bgra_image& image,
    std::span<const normalized_mask_rect> regions) noexcept;

[[nodiscard]] cloud_crop_encode_result encode_png(
    const bgra_image& image,
    std::size_t maximum_encoded_bytes) noexcept;
}
