#include <infini/ocr/cloud_crop_encoder.hpp>

#include <objidl.h>
#include <wincodec.h>
#include <windows.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <limits>
#include <stdexcept>

namespace infini::ocr
{
using Microsoft::WRL::ComPtr;

bool bgra_image::valid() const noexcept
{
    if (width == 0U || height == 0U || stride < width * 4ULL) return false;
    const std::uint64_t required = static_cast<std::uint64_t>(stride) * height;
    return required == pixels.size() && required <= (std::numeric_limits<std::size_t>::max)();
}

bgra_image downscale_bgra(
    const bgra_image& source,
    const std::uint32_t maximum_long_edge)
{
    if (!source.valid() || maximum_long_edge == 0U)
        throw std::invalid_argument("BGRA downscale input is invalid");
    const std::uint32_t source_long_edge = (std::max)(source.width, source.height);
    if (source_long_edge <= maximum_long_edge) return source;
    const double scale = static_cast<double>(maximum_long_edge) / source_long_edge;
    const std::uint32_t width = (std::max)(1U, static_cast<std::uint32_t>(
        std::lround(source.width * scale)));
    const std::uint32_t height = (std::max)(1U, static_cast<std::uint32_t>(
        std::lround(source.height * scale)));
    bgra_image result{width, height, width * 4U,
        std::vector<std::byte>(static_cast<std::size_t>(width) * height * 4U)};
    for (std::uint32_t y{}; y < height; ++y)
    {
        const double source_y = ((static_cast<double>(y) + 0.5) * source.height / height) - 0.5;
        const std::uint32_t y0 = static_cast<std::uint32_t>((std::clamp)(
            std::floor(source_y), 0.0, static_cast<double>(source.height - 1U)));
        const std::uint32_t y1 = (std::min)(y0 + 1U, source.height - 1U);
        const double fy = (std::clamp)(source_y - std::floor(source_y), 0.0, 1.0);
        for (std::uint32_t x{}; x < width; ++x)
        {
            const double source_x = ((static_cast<double>(x) + 0.5) * source.width / width) - 0.5;
            const std::uint32_t x0 = static_cast<std::uint32_t>((std::clamp)(
                std::floor(source_x), 0.0, static_cast<double>(source.width - 1U)));
            const std::uint32_t x1 = (std::min)(x0 + 1U, source.width - 1U);
            const double fx = (std::clamp)(source_x - std::floor(source_x), 0.0, 1.0);
            for (std::size_t channel{}; channel < 4U; ++channel)
            {
                const auto sample = [&](const std::uint32_t sx, const std::uint32_t sy)
                {
                    return static_cast<double>(std::to_integer<std::uint8_t>(source.pixels[
                        static_cast<std::size_t>(sy) * source.stride +
                        static_cast<std::size_t>(sx) * 4U + channel]));
                };
                const double top = sample(x0, y0) * (1.0 - fx) + sample(x1, y0) * fx;
                const double bottom = sample(x0, y1) * (1.0 - fx) + sample(x1, y1) * fx;
                result.pixels[static_cast<std::size_t>(y) * result.stride +
                    static_cast<std::size_t>(x) * 4U + channel] = static_cast<std::byte>(
                        static_cast<std::uint8_t>(std::lround(top * (1.0 - fy) + bottom * fy)));
            }
        }
    }
    return result;
}

bool mask_bgra_regions(
    bgra_image& image,
    const std::span<const normalized_mask_rect> regions) noexcept
{
    if (!image.valid()) return false;
    for (const normalized_mask_rect& region : regions)
    {
        if (!std::isfinite(region.x) || !std::isfinite(region.y) ||
            !std::isfinite(region.width) || !std::isfinite(region.height) ||
            region.x < 0.0 || region.y < 0.0 ||
            region.width <= 0.0 || region.height <= 0.0 ||
            region.x + region.width > 1.0 ||
            region.y + region.height > 1.0)
            return false;
    }
    for (const normalized_mask_rect& region : regions)
    {
        const std::uint32_t left = static_cast<std::uint32_t>((std::clamp)(
            std::floor(region.x * image.width), 0.0,
            static_cast<double>(image.width)));
        const std::uint32_t top = static_cast<std::uint32_t>((std::clamp)(
            std::floor(region.y * image.height), 0.0,
            static_cast<double>(image.height)));
        const std::uint32_t right = static_cast<std::uint32_t>((std::clamp)(
            std::ceil((region.x + region.width) * image.width - 1e-9), 0.0,
            static_cast<double>(image.width)));
        const std::uint32_t bottom = static_cast<std::uint32_t>((std::clamp)(
            std::ceil((region.y + region.height) * image.height - 1e-9), 0.0,
            static_cast<double>(image.height)));
        for (std::uint32_t y = top; y < bottom; ++y)
        {
            for (std::uint32_t x = left; x < right; ++x)
            {
                const std::size_t offset = static_cast<std::size_t>(y) *
                    image.stride + static_cast<std::size_t>(x) * 4U;
                image.pixels[offset] = std::byte{};
                image.pixels[offset + 1U] = std::byte{};
                image.pixels[offset + 2U] = std::byte{};
                image.pixels[offset + 3U] = std::byte{255};
            }
        }
    }
    return true;
}

cloud_crop_encode_result encode_png(
    const bgra_image& image,
    const std::size_t maximum_encoded_bytes) noexcept
{
    if (!image.valid() || maximum_encoded_bytes == 0U ||
        image.stride > (std::numeric_limits<UINT>::max)() ||
        image.pixels.size() > (std::numeric_limits<UINT>::max)())
        return {cloud_crop_encode_status::invalid_image, {}};
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitialize = SUCCEEDED(apartment);
    if (FAILED(apartment) && apartment != RPC_E_CHANGED_MODE)
        return {cloud_crop_encode_status::com_unavailable, {}};
    cloud_crop_encode_result result{cloud_crop_encode_status::encoding_failed, {}};
    try
    {
        ComPtr<IWICImagingFactory> factory;
        ComPtr<IStream> stream;
        ComPtr<IWICBitmapEncoder> encoder;
        ComPtr<IWICBitmapFrameEncode> frame;
        if (FAILED(CoCreateInstance(CLSID_WICImagingFactory, nullptr,
                CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&factory))) ||
            FAILED(CreateStreamOnHGlobal(nullptr, TRUE, &stream)) ||
            FAILED(factory->CreateEncoder(GUID_ContainerFormatPng, nullptr, &encoder)) ||
            FAILED(encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache)) ||
            FAILED(encoder->CreateNewFrame(&frame, nullptr)) ||
            FAILED(frame->Initialize(nullptr)) ||
            FAILED(frame->SetSize(image.width, image.height)))
            throw std::runtime_error("WIC PNG initialization failed");
        WICPixelFormatGUID format = GUID_WICPixelFormat32bppBGRA;
        if (FAILED(frame->SetPixelFormat(&format)) ||
            !IsEqualGUID(format, GUID_WICPixelFormat32bppBGRA) ||
            FAILED(frame->WritePixels(
                image.height,
                image.stride,
                static_cast<UINT>(image.pixels.size()),
                reinterpret_cast<BYTE*>(const_cast<std::byte*>(image.pixels.data())))) ||
            FAILED(frame->Commit()) || FAILED(encoder->Commit()))
            throw std::runtime_error("WIC PNG encoding failed");
        HGLOBAL memory{};
        if (FAILED(GetHGlobalFromStream(stream.Get(), &memory)) || memory == nullptr)
            throw std::runtime_error("WIC PNG stream is unavailable");
        const SIZE_T size = GlobalSize(memory);
        if (size == 0U || size > maximum_encoded_bytes)
        {
            result.status = cloud_crop_encode_status::capacity_rejected;
            throw std::length_error("WIC PNG exceeds capacity");
        }
        const void* const data = GlobalLock(memory);
        if (data == nullptr) throw std::runtime_error("WIC PNG stream lock failed");
        try
        {
            const auto* first = static_cast<const std::byte*>(data);
            result.bytes.assign(first, first + size);
            result.status = cloud_crop_encode_status::succeeded;
        }
        catch (...)
        {
            GlobalUnlock(memory);
            throw;
        }
        GlobalUnlock(memory);
    }
    catch (const std::length_error&)
    {
        if (result.status != cloud_crop_encode_status::capacity_rejected)
            result.status = cloud_crop_encode_status::encoding_failed;
        result.bytes.clear();
    }
    catch (...)
    {
        result.status = cloud_crop_encode_status::encoding_failed;
        result.bytes.clear();
    }
    if (uninitialize) CoUninitialize();
    return result;
}
}
