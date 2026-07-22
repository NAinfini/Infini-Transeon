#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace infini::ocr
{
struct rgba_image
{
    std::int32_t width{};
    std::int32_t height{};
    std::vector<std::uint8_t> pixels{};
};

struct grayscale_image
{
    std::int32_t width{};
    std::int32_t height{};
    std::vector<std::uint8_t> pixels{};
};

struct color_isolation_options
{
    std::uint8_t red{};
    std::uint8_t green{};
    std::uint8_t blue{};
    std::uint8_t tolerance{};
};

struct preprocessing_options
{
    std::uint32_t scale{1U};
    double contrast{1.0};
    bool adaptive_threshold{};
    std::uint32_t adaptive_radius{4U};
    std::optional<color_isolation_options> color_isolation{};
    bool sharpen{};
    bool outline_suppression{};
    bool invert{};
    bool alpha_cleanup{};
    std::uint8_t alpha_threshold{16U};
};

struct preprocessing_parse_result
{
    bool valid{};
    bool enabled{};
    preprocessing_options options{};
    std::string error_code{};
};

[[nodiscard]] preprocessing_parse_result parse_preprocessing_pipeline(
    std::string_view serialized_steps) noexcept;

[[nodiscard]] grayscale_image preprocess(
    const rgba_image& source,
    const preprocessing_options& options);
}
