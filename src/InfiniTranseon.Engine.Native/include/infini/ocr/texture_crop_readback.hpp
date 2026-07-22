#pragma once

#ifdef _WIN32

#include <infini/capture/windows_capture_source.hpp>
#include <infini/imaging/device_runtime.hpp>
#include <infini/ocr/cloud_crop_encoder.hpp>

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <memory>

namespace infini::ocr
{
struct texture_crop_rect final
{
    std::uint32_t x{};
    std::uint32_t y{};
    std::uint32_t width{};
    std::uint32_t height{};
};

enum class texture_crop_readback_status
{
    succeeded,
    invalid_input,
    capacity_rejected,
    queue_rejected,
    timed_out,
    device_failed,
};

struct texture_crop_readback_result final
{
    texture_crop_readback_status status{texture_crop_readback_status::invalid_input};
    bgra_image image;
};

[[nodiscard]] texture_crop_readback_result readback_texture_crop(
    ID3D11Device* device,
    imaging::device_runtime& runtime,
    const std::shared_ptr<capture::captured_frame>& frame,
    texture_crop_rect crop,
    std::size_t maximum_bytes,
    std::chrono::milliseconds timeout) noexcept;
}

#endif
