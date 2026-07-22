#include "infini/overlay/background_treatment.hpp"

#ifdef _WIN32
#include <windows.h>
#endif

#include <algorithm>
#include <cmath>
#include <limits>
#include <set>
#include <stdexcept>
#include <utility>
#include <vector>

namespace infini::overlay {

background_frame::~background_frame() noexcept {
    if (pixels.empty()) return;
#ifdef _WIN32
    SecureZeroMemory(pixels.data(), pixels.size());
#else
    std::fill(pixels.begin(), pixels.end(), std::byte{});
#endif
}

background_result resolve_background(
    const region_style& style,
    const color_rgba sampled_background,
    const bool temporal_cache_available) noexcept {
    if (style.background == background_mode::no_cover) {
        return {color_rgba{0.0F, 0.0F, 0.0F, 0.0F}, style.text_color, false, false};
    }
    if (style.background == background_mode::temporal_cache && temporal_cache_available) {
        return {sampled_background, style.text_color, true, true};
    }
    if (style.background == background_mode::automatic_contrast) {
        const float luminance = 0.2126F * sampled_background.red +
            0.7152F * sampled_background.green + 0.0722F * sampled_background.blue;
        const color_rgba text = luminance > 0.45F
            ? color_rgba{0.0F, 0.0F, 0.0F, 1.0F}
            : color_rgba{1.0F, 1.0F, 1.0F, 1.0F};
        color_rgba background = sampled_background;
        background.alpha = std::clamp(style.background_color.alpha, 0.25F, 0.9F);
        return {background, text, false, true};
    }
    return {style.background_color, style.text_color, false,
            style.background != background_mode::offset &&
                style.background != background_mode::floating_panel};
}

namespace {

[[nodiscard]] bool valid_dimensions(
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t stride,
    const std::size_t byte_count,
    const std::size_t maximum_bytes) noexcept {
    if (width == 0U || height == 0U || width > 16'384U || height > 16'384U ||
        maximum_bytes == 0U || width > (std::numeric_limits<std::uint32_t>::max)() / 4U ||
        stride < width * 4U || height > maximum_bytes / stride) return false;
    const std::size_t required = static_cast<std::size_t>(stride) * height;
    return required == byte_count && required <= maximum_bytes;
}

[[nodiscard]] color_rgba average_color(
    const std::span<const std::byte> pixels,
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t stride) noexcept {
    std::uint64_t blue{};
    std::uint64_t green{};
    std::uint64_t red{};
    std::uint64_t alpha{};
    for (std::uint32_t y{}; y < height; ++y) {
        const std::size_t row = static_cast<std::size_t>(y) * stride;
        for (std::uint32_t x{}; x < width; ++x) {
            const std::size_t offset = row + static_cast<std::size_t>(x) * 4U;
            blue += std::to_integer<std::uint8_t>(pixels[offset]);
            green += std::to_integer<std::uint8_t>(pixels[offset + 1U]);
            red += std::to_integer<std::uint8_t>(pixels[offset + 2U]);
            alpha += std::to_integer<std::uint8_t>(pixels[offset + 3U]);
        }
    }
    const float denominator = static_cast<float>(
        static_cast<std::uint64_t>(width) * height * 255U);
    return {
        static_cast<float>(red) / denominator,
        static_cast<float>(green) / denominator,
        static_cast<float>(blue) / denominator,
        static_cast<float>(alpha) / denominator,
    };
}

} // namespace

std::shared_ptr<const background_frame> make_background_frame(
    const std::span<const std::byte> bgra_pixels,
    const std::uint32_t width,
    const std::uint32_t height,
    const std::uint32_t stride,
    const std::size_t maximum_bytes) noexcept {
    try {
        if (!valid_dimensions(
                width, height, stride, bgra_pixels.size(), maximum_bytes)) return {};
        auto result = std::make_shared<background_frame>();
        result->width = width;
        result->height = height;
        result->stride = stride;
        result->pixels.assign(bgra_pixels.begin(), bgra_pixels.end());
        result->average = average_color(result->pixels, width, height, stride);
        return result;
    } catch (...) {
        return {};
    }
}

std::shared_ptr<const background_frame> crop_background_frame(
    const background_frame& source,
    const rect_f crop,
    const rect_f source_extent,
    const std::size_t maximum_bytes) noexcept {
    try {
        if (!valid_dimensions(source.width, source.height, source.stride,
                source.pixels.size(), maximum_bytes) ||
            !std::isfinite(crop.x) || !std::isfinite(crop.y) ||
            !std::isfinite(crop.width) || !std::isfinite(crop.height) ||
            !std::isfinite(source_extent.x) || !std::isfinite(source_extent.y) ||
            !std::isfinite(source_extent.width) || !std::isfinite(source_extent.height) ||
            crop.width <= 0.0F || crop.height <= 0.0F ||
            source_extent.width <= 0.0F || source_extent.height <= 0.0F) return {};
        const auto map_x = [&](const float value) {
            return (value - source_extent.x) / source_extent.width *
                static_cast<float>(source.width);
        };
        const auto map_y = [&](const float value) {
            return (value - source_extent.y) / source_extent.height *
                static_cast<float>(source.height);
        };
        const std::uint32_t left = static_cast<std::uint32_t>((std::clamp)(
            std::floor(map_x(crop.x)), 0.0F, static_cast<float>(source.width)));
        const std::uint32_t top = static_cast<std::uint32_t>((std::clamp)(
            std::floor(map_y(crop.y)), 0.0F, static_cast<float>(source.height)));
        const std::uint32_t right = static_cast<std::uint32_t>((std::clamp)(
            std::ceil(map_x(crop.x + crop.width)), 0.0F, static_cast<float>(source.width)));
        const std::uint32_t bottom = static_cast<std::uint32_t>((std::clamp)(
            std::ceil(map_y(crop.y + crop.height)), 0.0F, static_cast<float>(source.height)));
        if (right <= left || bottom <= top) return {};
        const std::uint32_t width = right - left;
        const std::uint32_t height = bottom - top;
        if (width > (std::numeric_limits<std::uint32_t>::max)() / 4U) return {};
        const std::uint32_t stride = width * 4U;
        if (height > maximum_bytes / stride) return {};
        std::vector<std::byte> pixels(static_cast<std::size_t>(stride) * height);
        for (std::uint32_t y{}; y < height; ++y) {
            const std::size_t source_offset = static_cast<std::size_t>(top + y) *
                source.stride + static_cast<std::size_t>(left) * 4U;
            const std::size_t destination_offset = static_cast<std::size_t>(y) * stride;
            std::ranges::copy_n(
                source.pixels.begin() + static_cast<std::ptrdiff_t>(source_offset),
                stride,
                pixels.begin() + static_cast<std::ptrdiff_t>(destination_offset));
        }
        return make_background_frame(pixels, width, height, stride, maximum_bytes);
    } catch (...) {
        return {};
    }
}

std::shared_ptr<const background_frame> blur_background_frame(
    const background_frame& source,
    const std::uint32_t radius,
    const std::size_t maximum_bytes) noexcept {
    try {
        if (!valid_dimensions(source.width, source.height, source.stride,
                source.pixels.size(), maximum_bytes)) return {};
        if (radius == 0U) return std::make_shared<const background_frame>(source);
        const std::uint32_t bounded_radius = (std::min)(radius, 64U);
        std::vector<std::byte> horizontal(source.pixels.size());
        std::vector<std::byte> output(source.pixels.size());
        for (std::uint32_t y{}; y < source.height; ++y) {
            for (std::uint32_t channel{}; channel < 4U; ++channel) {
                std::uint64_t sum{};
                const std::uint32_t initial_right = (std::min)(bounded_radius, source.width - 1U);
                for (std::uint32_t x{}; x <= initial_right; ++x)
                    sum += std::to_integer<std::uint8_t>(source.pixels[
                        static_cast<std::size_t>(y) * source.stride +
                        static_cast<std::size_t>(x) * 4U + channel]);
                for (std::uint32_t x{}; x < source.width; ++x) {
                    const std::uint32_t left = x > bounded_radius ? x - bounded_radius : 0U;
                    const std::uint32_t right = (std::min)(
                        source.width - 1U, x + bounded_radius);
                    horizontal[static_cast<std::size_t>(y) * source.stride +
                        static_cast<std::size_t>(x) * 4U + channel] =
                        static_cast<std::byte>(sum / (right - left + 1U));
                    if (x >= bounded_radius)
                        sum -= std::to_integer<std::uint8_t>(source.pixels[
                            static_cast<std::size_t>(y) * source.stride +
                            static_cast<std::size_t>(x - bounded_radius) * 4U + channel]);
                    if (x + bounded_radius + 1U < source.width)
                        sum += std::to_integer<std::uint8_t>(source.pixels[
                            static_cast<std::size_t>(y) * source.stride +
                            static_cast<std::size_t>(x + bounded_radius + 1U) * 4U + channel]);
                }
            }
        }
        for (std::uint32_t x{}; x < source.width; ++x) {
            for (std::uint32_t channel{}; channel < 4U; ++channel) {
                std::uint64_t sum{};
                const std::uint32_t initial_bottom = (std::min)(bounded_radius, source.height - 1U);
                for (std::uint32_t y{}; y <= initial_bottom; ++y)
                    sum += std::to_integer<std::uint8_t>(horizontal[
                        static_cast<std::size_t>(y) * source.stride +
                        static_cast<std::size_t>(x) * 4U + channel]);
                for (std::uint32_t y{}; y < source.height; ++y) {
                    const std::uint32_t top = y > bounded_radius ? y - bounded_radius : 0U;
                    const std::uint32_t bottom = (std::min)(
                        source.height - 1U, y + bounded_radius);
                    output[static_cast<std::size_t>(y) * source.stride +
                        static_cast<std::size_t>(x) * 4U + channel] =
                        static_cast<std::byte>(sum / (bottom - top + 1U));
                    if (y >= bounded_radius)
                        sum -= std::to_integer<std::uint8_t>(horizontal[
                            static_cast<std::size_t>(y - bounded_radius) * source.stride +
                            static_cast<std::size_t>(x) * 4U + channel]);
                    if (y + bounded_radius + 1U < source.height)
                        sum += std::to_integer<std::uint8_t>(horizontal[
                            static_cast<std::size_t>(y + bounded_radius + 1U) * source.stride +
                            static_cast<std::size_t>(x) * 4U + channel]);
                }
            }
        }
        return make_background_frame(
            output, source.width, source.height, source.stride, maximum_bytes);
    } catch (...) {
        return {};
    }
}

background_frame_cache::background_frame_cache(const std::size_t maximum_bytes)
    : maximum_bytes_(maximum_bytes) {
    if (maximum_bytes == 0U) throw std::invalid_argument("background cache must be bounded");
}

bool background_frame_cache::update(
    const identity& key,
    std::shared_ptr<const background_frame> frame,
    const bool confirmed_clean) noexcept {
    try {
        if (!frame || frame->pixels.empty() || frame->pixels.size() > maximum_bytes_) return false;
        entry& value = entries_[key];
        value.latest = std::move(frame);
        if (confirmed_clean) value.clean = value.latest;
        value.access = ++access_;
        trim(key);
        return value.latest != nullptr;
    } catch (...) {
        return false;
    }
}

std::shared_ptr<const background_frame> background_frame_cache::snapshot(
    const identity& key,
    const bool require_clean) noexcept {
    const auto found = entries_.find(key);
    if (found == entries_.end()) return {};
    found->second.access = ++access_;
    return require_clean ? found->second.clean : found->second.latest;
}

std::size_t background_frame_cache::retained_bytes() const noexcept {
    std::set<const background_frame*> unique;
    std::size_t total{};
    for (const auto& [_, value] : entries_) {
        for (const auto& frame : {value.latest, value.clean}) {
            if (frame && unique.insert(frame.get()).second) total += frame->pixels.size();
        }
    }
    return total;
}

void background_frame_cache::clear() noexcept {
    entries_.clear();
    access_ = 0U;
}

void background_frame_cache::trim(const identity& protected_key) noexcept {
    while (retained_bytes() > maximum_bytes_) {
        auto oldest = entries_.end();
        for (auto iterator = entries_.begin(); iterator != entries_.end(); ++iterator) {
            if (iterator->first == protected_key) continue;
            if (oldest == entries_.end() || iterator->second.access < oldest->second.access)
                oldest = iterator;
        }
        if (oldest != entries_.end()) {
            entries_.erase(oldest);
            continue;
        }
        entry& protected_entry = entries_.at(protected_key);
        if (protected_entry.clean && protected_entry.clean != protected_entry.latest) {
            protected_entry.clean.reset();
            continue;
        }
        break;
    }
}

std::shared_ptr<const background_frame> background_blur_cache::get(
    const std::shared_ptr<const background_frame>& source,
    const std::uint32_t radius,
    const std::size_t maximum_bytes) noexcept {
    if (!source) return {};
    const auto key = std::pair{source.get(), radius};
    {
        std::scoped_lock lock(gate_);
        const auto found = entries_.find(key);
        if (found != entries_.end() && found->second.source.lock() == source) {
            if (const auto existing = found->second.blurred.lock()) return existing;
        }
    }
    const auto blurred = blur_background_frame(*source, radius, maximum_bytes);
    if (!blurred) return {};
    {
        std::scoped_lock lock(gate_);
        entries_.insert_or_assign(key, entry{source, blurred});
        std::erase_if(entries_, [](const auto& item) {
            return item.second.source.expired() || item.second.blurred.expired();
        });
    }
    return blurred;
}

void background_blur_cache::clear() noexcept {
    std::scoped_lock lock(gate_);
    entries_.clear();
}

} // namespace infini::overlay
