#include <infini/ocr/texture_crop_readback.hpp>

#ifdef _WIN32

#include <algorithm>
#include <condition_variable>
#include <limits>
#include <mutex>

namespace infini::ocr
{
namespace
{
struct readback_state final
{
    std::mutex gate;
    std::condition_variable changed;
    bool completed{};
    texture_crop_readback_status status{texture_crop_readback_status::device_failed};
    bgra_image image;
};
}

texture_crop_readback_result readback_texture_crop(
    ID3D11Device* const device,
    imaging::device_runtime& runtime,
    const std::shared_ptr<capture::captured_frame>& frame,
    const texture_crop_rect crop,
    const std::size_t maximum_bytes,
    const std::chrono::milliseconds timeout) noexcept
{
    if (device == nullptr || !frame || !frame->texture || !frame->lease ||
        frame->identity.device_epoch == 0U || crop.width == 0U || crop.height == 0U ||
        crop.x > frame->width || crop.y > frame->height ||
        crop.width > frame->width - crop.x || crop.height > frame->height - crop.y ||
        timeout <= std::chrono::milliseconds::zero())
        return {texture_crop_readback_status::invalid_input, {}};
    const std::uint64_t stride = static_cast<std::uint64_t>(crop.width) * 4U;
    const std::uint64_t byte_count = stride * crop.height;
    if (byte_count == 0U || byte_count > maximum_bytes ||
        byte_count > (std::numeric_limits<std::size_t>::max)() ||
        stride > (std::numeric_limits<std::uint32_t>::max)())
        return {texture_crop_readback_status::capacity_rejected, {}};

    D3D11_TEXTURE2D_DESC source_description{};
    frame->texture->GetDesc(&source_description);
    if ((source_description.Format != DXGI_FORMAT_B8G8R8A8_UNORM &&
            source_description.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB) ||
        source_description.SampleDesc.Count != 1U)
        return {texture_crop_readback_status::invalid_input, {}};
    D3D11_TEXTURE2D_DESC staging_description{};
    staging_description.Width = crop.width;
    staging_description.Height = crop.height;
    staging_description.MipLevels = 1U;
    staging_description.ArraySize = 1U;
    staging_description.Format = source_description.Format;
    staging_description.SampleDesc.Count = 1U;
    staging_description.Usage = D3D11_USAGE_STAGING;
    staging_description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
    Microsoft::WRL::ComPtr<ID3D11Texture2D> staging;
    if (FAILED(device->CreateTexture2D(&staging_description, nullptr, &staging)))
        return {texture_crop_readback_status::device_failed, {}};

    auto state = std::make_shared<readback_state>();
    const D3D11_BOX source_box{
        crop.x,
        crop.y,
        0U,
        crop.x + crop.width,
        crop.y + crop.height,
        1U,
    };
    const bool queued = runtime.enqueue(
        frame->lease->acquire_ticket(),
        [state, staging, source = frame->texture, source_box, crop, byte_count]
        (ID3D11DeviceContext* context) -> HRESULT
        {
            context->CopySubresourceRegion(
                staging.Get(), 0U, 0U, 0U, 0U, source.Get(), 0U, &source_box);
            D3D11_MAPPED_SUBRESOURCE mapped{};
            const HRESULT mapped_result = context->Map(
                staging.Get(), 0U, D3D11_MAP_READ, 0U, &mapped);
            if (FAILED(mapped_result)) return mapped_result;
            HRESULT result = S_OK;
            try
            {
                state->image = bgra_image{
                    crop.width,
                    crop.height,
                    crop.width * 4U,
                    std::vector<std::byte>(static_cast<std::size_t>(byte_count)),
                };
                for (std::uint32_t row{}; row < crop.height; ++row)
                {
                    const auto* source_row = static_cast<const std::byte*>(mapped.pData) +
                        static_cast<std::size_t>(row) * mapped.RowPitch;
                    std::copy_n(source_row, state->image.stride,
                        state->image.pixels.begin() +
                            static_cast<std::size_t>(row) * state->image.stride);
                }
            }
            catch (...)
            {
                state->image = {};
                result = E_OUTOFMEMORY;
            }
            context->Unmap(staging.Get(), 0U);
            return result;
        },
        [state](const imaging::device_submission_result submission) noexcept
        {
            {
                std::scoped_lock lock(state->gate);
                state->status = submission.status ==
                        imaging::device_submission_status::completed && state->image.valid()
                    ? texture_crop_readback_status::succeeded
                    : texture_crop_readback_status::device_failed;
                state->completed = true;
            }
            state->changed.notify_one();
        });
    if (!queued) return {texture_crop_readback_status::queue_rejected, {}};
    std::unique_lock lock(state->gate);
    if (!state->changed.wait_for(lock, timeout, [&] { return state->completed; }))
        return {texture_crop_readback_status::timed_out, {}};
    return {state->status, std::move(state->image)};
}
}

#endif
