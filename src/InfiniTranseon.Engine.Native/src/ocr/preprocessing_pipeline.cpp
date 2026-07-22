#include <infini/ocr/preprocessing_pipeline.hpp>

#include <algorithm>
#include <charconv>
#include <cmath>
#include <cstddef>
#include <limits>
#include <stdexcept>

namespace infini::ocr
{
namespace
{
bool parse_u32(const std::string_view text, std::uint32_t& value) noexcept
{
    if (text.empty()) return false;
    const auto [end, error] = std::from_chars(text.data(), text.data() + text.size(), value);
    return error == std::errc{} && end == text.data() + text.size();
}

bool parse_double(const std::string_view text, double& value) noexcept
{
    if (text.empty()) return false;
    const auto [end, error] = std::from_chars(
        text.data(), text.data() + text.size(), value, std::chars_format::general);
    return error == std::errc{} && end == text.data() + text.size() && std::isfinite(value);
}

std::optional<std::uint8_t> hex_byte(const std::string_view text) noexcept
{
    if (text.size() != 2U) return std::nullopt;
    unsigned int value{};
    const auto [end, error] = std::from_chars(
        text.data(), text.data() + text.size(), value, 16);
    if (error != std::errc{} || end != text.data() + text.size() || value > 255U)
        return std::nullopt;
    return static_cast<std::uint8_t>(value);
}

bool apply_step(const std::string_view step, preprocessing_options& options) noexcept
{
    if (step == "grayscale") return true;
    if (step == "threshold" || step == "adaptive-threshold")
    {
        options.adaptive_threshold = true;
        return true;
    }
    if (step == "sharpen") { options.sharpen = true; return true; }
    if (step == "outline-suppression") { options.outline_suppression = true; return true; }
    if (step == "invert") { options.invert = true; return true; }
    if (step == "alpha-cleanup") { options.alpha_cleanup = true; return true; }

    constexpr std::string_view contrast_prefix = "contrast:";
    if (step.starts_with(contrast_prefix))
    {
        double value{};
        if (!parse_double(step.substr(contrast_prefix.size()), value) ||
            value < 0.1 || value > 4.0) return false;
        options.contrast = value;
        return true;
    }
    constexpr std::string_view threshold_prefix = "adaptive-threshold:";
    if (step.starts_with(threshold_prefix))
    {
        std::uint32_t radius{};
        if (!parse_u32(step.substr(threshold_prefix.size()), radius) ||
            radius == 0U || radius > 64U) return false;
        options.adaptive_threshold = true;
        options.adaptive_radius = radius;
        return true;
    }
    constexpr std::string_view scale_prefix = "scale:";
    if (step.starts_with(scale_prefix))
    {
        std::uint32_t scale{};
        if (!parse_u32(step.substr(scale_prefix.size()), scale) ||
            scale == 0U || scale > 8U) return false;
        options.scale = scale;
        return true;
    }
    constexpr std::string_view alpha_prefix = "alpha-cleanup:";
    if (step.starts_with(alpha_prefix))
    {
        std::uint32_t threshold{};
        if (!parse_u32(step.substr(alpha_prefix.size()), threshold) || threshold > 255U)
            return false;
        options.alpha_cleanup = true;
        options.alpha_threshold = static_cast<std::uint8_t>(threshold);
        return true;
    }
    constexpr std::string_view color_prefix = "color-isolation:#";
    if (step.starts_with(color_prefix))
    {
        const std::string_view value = step.substr(color_prefix.size());
        if (value.size() < 8U || value[6U] != ':') return false;
        const auto red = hex_byte(value.substr(0U, 2U));
        const auto green = hex_byte(value.substr(2U, 2U));
        const auto blue = hex_byte(value.substr(4U, 2U));
        std::uint32_t tolerance{};
        if (!red || !green || !blue ||
            !parse_u32(value.substr(7U), tolerance) || tolerance > 255U)
            return false;
        options.color_isolation = color_isolation_options{
            *red, *green, *blue, static_cast<std::uint8_t>(tolerance)};
        return true;
    }
    return false;
}

std::size_t checked_pixels(const std::int32_t width, const std::int32_t height)
{
    if (width <= 0 || height <= 0) throw std::invalid_argument("image dimensions must be positive");
    const auto count = static_cast<std::uint64_t>(width) * static_cast<std::uint64_t>(height);
    if (count > std::numeric_limits<std::size_t>::max()) throw std::length_error("image is too large");
    return static_cast<std::size_t>(count);
}

std::uint8_t clamp_byte(const double value) noexcept
{
    return static_cast<std::uint8_t>(std::clamp(std::lround(value), 0L, 255L));
}

std::vector<std::uint8_t> median_filter(
    const std::vector<std::uint8_t>& source,
    const std::int32_t width,
    const std::int32_t height)
{
    std::vector<std::uint8_t> result(source.size());
    for (std::int32_t y = 0; y < height; ++y)
    {
        for (std::int32_t x = 0; x < width; ++x)
        {
            std::uint8_t values[9]{};
            std::size_t count = 0U;
            for (std::int32_t offset_y = -1; offset_y <= 1; ++offset_y)
            {
                for (std::int32_t offset_x = -1; offset_x <= 1; ++offset_x)
                {
                    const std::int32_t sample_x = std::clamp(x + offset_x, 0, width - 1);
                    const std::int32_t sample_y = std::clamp(y + offset_y, 0, height - 1);
                    values[count++] = source[static_cast<std::size_t>(sample_y) * width + sample_x];
                }
            }
            std::nth_element(values, values + 4, values + count);
            result[static_cast<std::size_t>(y) * width + x] = values[4];
        }
    }
    return result;
}
}

preprocessing_parse_result parse_preprocessing_pipeline(
    const std::string_view serialized_steps) noexcept
{
    preprocessing_parse_result result{};
    result.error_code = "ocr.preprocessing.invalidPipeline";
    if (serialized_steps.size() < 2U || serialized_steps.front() != '[' ||
        serialized_steps.back() != ']') return result;
    std::size_t offset = 1U;
    while (offset < serialized_steps.size() - 1U)
    {
        while (offset < serialized_steps.size() - 1U && serialized_steps[offset] == ' ')
            ++offset;
        if (offset == serialized_steps.size() - 1U) break;
        if (serialized_steps[offset] != '"') return result;
        const std::size_t end = serialized_steps.find('"', offset + 1U);
        if (end == std::string_view::npos || end >= serialized_steps.size() - 1U)
            return result;
        const std::string_view step = serialized_steps.substr(offset + 1U, end - offset - 1U);
        if (step.empty() || step.find('\\') != std::string_view::npos ||
            !apply_step(step, result.options)) return result;
        result.enabled = true;
        offset = end + 1U;
        while (offset < serialized_steps.size() - 1U && serialized_steps[offset] == ' ')
            ++offset;
        if (offset == serialized_steps.size() - 1U) break;
        if (serialized_steps[offset] != ',') return result;
        ++offset;
    }
    result.valid = offset == serialized_steps.size() - 1U;
    if (result.valid) result.error_code.clear();
    return result;
}

grayscale_image preprocess(const rgba_image& source, const preprocessing_options& options)
{
    const std::size_t pixel_count = checked_pixels(source.width, source.height);
    if (source.pixels.size() != pixel_count * 4U)
        throw std::invalid_argument("RGBA byte count does not match image dimensions");
    if (options.scale == 0U || options.scale > 8U || !std::isfinite(options.contrast) ||
        options.contrast <= 0.0 || options.adaptive_radius > 64U)
    {
        throw std::invalid_argument("preprocessing options are outside supported bounds");
    }

    std::vector<std::uint8_t> pixels(pixel_count);
    for (std::size_t index = 0; index < pixel_count; ++index)
    {
        const std::uint8_t red = source.pixels[index * 4U];
        const std::uint8_t green = source.pixels[index * 4U + 1U];
        const std::uint8_t blue = source.pixels[index * 4U + 2U];
        const std::uint8_t alpha = source.pixels[index * 4U + 3U];
        double value = 0.0;
        if (options.color_isolation)
        {
            const auto& isolated = *options.color_isolation;
            const bool matches = std::abs(static_cast<int>(red) - isolated.red) <= isolated.tolerance &&
                std::abs(static_cast<int>(green) - isolated.green) <= isolated.tolerance &&
                std::abs(static_cast<int>(blue) - isolated.blue) <= isolated.tolerance;
            value = matches && alpha >= options.alpha_threshold ? 255.0 : 0.0;
        }
        else
        {
            value = (77.0 * red + 150.0 * green + 29.0 * blue) / 256.0;
            if (options.alpha_cleanup)
                value = alpha < options.alpha_threshold
                    ? 255.0
                    : (value * alpha + 255.0 * (255U - alpha)) / 255.0;
        }
        value = (value - 128.0) * options.contrast + 128.0;
        pixels[index] = clamp_byte(value);
    }

    if (options.outline_suppression) pixels = median_filter(pixels, source.width, source.height);
    if (options.sharpen)
    {
        const std::vector<std::uint8_t> blurred = median_filter(pixels, source.width, source.height);
        for (std::size_t index = 0; index < pixels.size(); ++index)
            pixels[index] = clamp_byte(2.0 * pixels[index] - blurred[index]);
    }
    if (options.adaptive_threshold)
    {
        std::vector<std::uint8_t> thresholded(pixels.size());
        const std::int32_t radius = static_cast<std::int32_t>(options.adaptive_radius);
        const std::size_t integral_width = static_cast<std::size_t>(source.width) + 1U;
        const std::size_t integral_height = static_cast<std::size_t>(source.height) + 1U;
        if (integral_width > std::numeric_limits<std::size_t>::max() / integral_height)
            throw std::length_error("adaptive-threshold integral image is too large");
        std::vector<std::uint64_t> integral(integral_width * integral_height);
        for (std::int32_t y = 0; y < source.height; ++y)
        {
            std::uint64_t row_sum{};
            for (std::int32_t x = 0; x < source.width; ++x)
            {
                row_sum += pixels[static_cast<std::size_t>(y) * source.width + x];
                integral[(static_cast<std::size_t>(y) + 1U) * integral_width +
                    static_cast<std::size_t>(x) + 1U] =
                    integral[static_cast<std::size_t>(y) * integral_width +
                        static_cast<std::size_t>(x) + 1U] + row_sum;
            }
        }
        for (std::int32_t y = 0; y < source.height; ++y)
        {
            for (std::int32_t x = 0; x < source.width; ++x)
            {
                const std::int32_t left = (std::max)(0, x - radius);
                const std::int32_t top = (std::max)(0, y - radius);
                const std::int32_t right = (std::min)(source.width - 1, x + radius) + 1;
                const std::int32_t bottom = (std::min)(source.height - 1, y + radius) + 1;
                const std::uint64_t sum =
                    integral[static_cast<std::size_t>(bottom) * integral_width + right] -
                    integral[static_cast<std::size_t>(top) * integral_width + right] -
                    integral[static_cast<std::size_t>(bottom) * integral_width + left] +
                    integral[static_cast<std::size_t>(top) * integral_width + left];
                const std::uint64_t count = static_cast<std::uint64_t>(right - left) *
                    static_cast<std::uint64_t>(bottom - top);
                const auto index = static_cast<std::size_t>(y) * source.width + x;
                thresholded[index] = pixels[index] < sum / count ? 0U : 255U;
            }
        }
        pixels = std::move(thresholded);
    }
    if (options.invert)
    {
        for (std::uint8_t& pixel : pixels) pixel = static_cast<std::uint8_t>(255U - pixel);
    }

    const auto output_width_64 = static_cast<std::int64_t>(source.width) * options.scale;
    const auto output_height_64 = static_cast<std::int64_t>(source.height) * options.scale;
    if (output_width_64 > std::numeric_limits<std::int32_t>::max() ||
        output_height_64 > std::numeric_limits<std::int32_t>::max())
        throw std::length_error("scaled image dimensions overflow");
    const std::int32_t output_width = static_cast<std::int32_t>(output_width_64);
    const std::int32_t output_height = static_cast<std::int32_t>(output_height_64);
    std::vector<std::uint8_t> scaled(checked_pixels(output_width, output_height));
    for (std::int32_t y = 0; y < output_height; ++y)
    {
        for (std::int32_t x = 0; x < output_width; ++x)
        {
            scaled[static_cast<std::size_t>(y) * output_width + x] =
                pixels[static_cast<std::size_t>(y / options.scale) * source.width + x / options.scale];
        }
    }
    return {output_width, output_height, std::move(scaled)};
}
}
