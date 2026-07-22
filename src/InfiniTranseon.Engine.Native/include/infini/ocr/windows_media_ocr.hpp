#pragma once

#include <infini/ocr/cloud_crop_encoder.hpp>
#include <infini/ocr/ocr_engine.hpp>

#include <string_view>
#include <vector>

namespace infini::ocr
{
enum class windows_media_ocr_status
{
    succeeded,
    invalid_input,
    language_unavailable,
    capacity_rejected,
    runtime_failed,
};

struct windows_media_ocr_result final
{
    windows_media_ocr_status status{windows_media_ocr_status::invalid_input};
    std::vector<ocr_line> lines;
    std::string error_code;
};

class windows_media_ocr final
{
public:
    [[nodiscard]] windows_media_ocr_result recognize(
        const bgra_image& image,
        std::string_view language_tag) const noexcept;
};
}
