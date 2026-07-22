#include <infini/ocr/windows_media_ocr.hpp>

#include <windows.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Globalization.h>
#include <winrt/Windows.Graphics.Imaging.h>
#include <winrt/Windows.Media.Ocr.h>
#include <winrt/Windows.Storage.Streams.h>
#include <winrt/base.h>

#include <algorithm>
#include <cmath>
#include <cstring>
#include <limits>
#include <string>

namespace infini::ocr
{
namespace
{
struct __declspec(uuid("905a0fef-bc53-11df-8c49-001e4fc686da"))
    IBufferByteAccess : IUnknown
{
    virtual HRESULT __stdcall Buffer(BYTE** value) = 0;
};
}

windows_media_ocr_result windows_media_ocr::recognize(
    const bgra_image& image,
    const std::string_view language_tag) const noexcept
{
    using namespace winrt::Windows::Globalization;
    using namespace winrt::Windows::Graphics::Imaging;
    using namespace winrt::Windows::Media::Ocr;
    using namespace winrt::Windows::Storage::Streams;
    if (!image.valid() || language_tag.empty() || language_tag.size() > 64U ||
        image.width > 10'000U || image.height > 10'000U)
        return {windows_media_ocr_status::invalid_input, {}, "ocr.windows.invalidInput"};
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninitialize = SUCCEEDED(apartment);
    if (FAILED(apartment) && apartment != RPC_E_CHANGED_MODE)
        return {windows_media_ocr_status::runtime_failed, {}, "ocr.windows.comUnavailable"};
    windows_media_ocr_result output{
        windows_media_ocr_status::runtime_failed, {}, "ocr.windows.runtimeFailed"};
    try
    {
        OcrEngine engine = language_tag == "auto"
            ? OcrEngine::TryCreateFromUserProfileLanguages()
            : OcrEngine::TryCreateFromLanguage(Language(winrt::to_hstring(language_tag)));
        if (!engine)
        {
            output = {windows_media_ocr_status::language_unavailable, {},
                "ocr.windows.languageUnavailable"};
        }
        else
        {
            const std::uint64_t tight_bytes =
                static_cast<std::uint64_t>(image.width) * image.height * 4U;
            if (tight_bytes > (std::numeric_limits<std::uint32_t>::max)())
            {
                output = {windows_media_ocr_status::capacity_rejected, {},
                    "ocr.windows.capacity"};
            }
            else
            {
                Buffer buffer(static_cast<std::uint32_t>(tight_bytes));
                buffer.Length(static_cast<std::uint32_t>(tight_bytes));
                BYTE* destination{};
                winrt::check_hresult(buffer.as<IBufferByteAccess>()->Buffer(&destination));
                const std::size_t tight_stride = static_cast<std::size_t>(image.width) * 4U;
                for (std::uint32_t row{}; row < image.height; ++row)
                    std::memcpy(destination + static_cast<std::size_t>(row) * tight_stride,
                        image.pixels.data() + static_cast<std::size_t>(row) * image.stride,
                        tight_stride);
                SoftwareBitmap bitmap(
                    BitmapPixelFormat::Bgra8,
                    static_cast<std::int32_t>(image.width),
                    static_cast<std::int32_t>(image.height),
                    BitmapAlphaMode::Premultiplied);
                bitmap.CopyFromBuffer(buffer);
                SecureZeroMemory(destination, static_cast<SIZE_T>(tight_bytes));
                const OcrResult result = engine.RecognizeAsync(bitmap).get();
                const auto angle = result.TextAngle();
                const std::int32_t orientation = angle
                    ? static_cast<std::int32_t>(std::lround(angle.Value()))
                    : 0;
                output.lines.reserve(result.Lines().Size());
                for (const OcrLine& line : result.Lines())
                {
                    if (output.lines.size() >= 2'048U)
                    {
                        output = {windows_media_ocr_status::capacity_rejected, {},
                            "ocr.windows.tooManyLines"};
                        break;
                    }
                    const auto words = line.Words();
                    if (words.Size() == 0U) continue;
                    float left = (std::numeric_limits<float>::max)();
                    float top = (std::numeric_limits<float>::max)();
                    float right{};
                    float bottom{};
                    for (const OcrWord& word : words)
                    {
                        const auto bounds = word.BoundingRect();
                        left = (std::min)(left, bounds.X);
                        top = (std::min)(top, bounds.Y);
                        right = (std::max)(right, bounds.X + bounds.Width);
                        bottom = (std::max)(bottom, bounds.Y + bounds.Height);
                    }
                    const double x = (std::clamp)(left / image.width, 0.0F, 1.0F);
                    const double y = (std::clamp)(top / image.height, 0.0F, 1.0F);
                    const double edge_x = (std::clamp)(right / image.width,
                        static_cast<float>(x), 1.0F);
                    const double edge_y = (std::clamp)(bottom / image.height,
                        static_cast<float>(y), 1.0F);
                    if (edge_x <= x || edge_y <= y) continue;
                    output.lines.push_back({
                        winrt::to_string(line.Text()),
                        {x, y, edge_x - x, edge_y - y},
                        0.5,
                        orientation,
                        std::abs(orientation) == 90,
                    });
                }
                if (output.status != windows_media_ocr_status::capacity_rejected)
                    output = {windows_media_ocr_status::succeeded,
                        std::move(output.lines), {}};
            }
        }
    }
    catch (const winrt::hresult_invalid_argument&)
    {
        output = {windows_media_ocr_status::language_unavailable, {},
            "ocr.windows.languageUnavailable"};
    }
    catch (...)
    {
        output = {windows_media_ocr_status::runtime_failed, {},
            "ocr.windows.runtimeFailed"};
    }
    if (uninitialize) CoUninitialize();
    return output;
}
}
