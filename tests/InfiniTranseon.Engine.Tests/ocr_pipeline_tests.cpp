#include <chrono>
#include <array>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <string>

#include <infini/ocr/ocr_engine.hpp>
#include <infini/ocr/cloud_crop_encoder.hpp>
#include <infini/ocr/onnx_session_pool.hpp>
#include <infini/ocr/preprocessing_pipeline.hpp>
#include <infini/ocr/readback_ring.hpp>
#include <infini/ocr/windows_media_ocr.hpp>

namespace
{
void require_at(const bool condition, const int line)
{
    if (!condition)
    {
        std::cerr << "OCR pipeline assertion failed at line " << line << '\n';
        std::abort();
    }
}

#define require(...) require_at((__VA_ARGS__), __LINE__)

class fake_backend final : public infini::ocr::local_ocr_backend
{
public:
    infini::ocr::ocr_result recognize(
        const infini::ocr::grayscale_image& image,
        const infini::ocr::model_descriptor& model,
        const infini::ocr::ocr_mode mode) override
    {
        require(image.width > 0 && image.height > 0);
        require(mode == infini::ocr::ocr_mode::fixed_region_recognizer);
        infini::ocr::ocr_result result{};
        result.lines.push_back({"tiny text", {0.0, 0.0, 1.0, 1.0}, 0.95, 90, true});
        result.model = {model.model_id, model.version, model.sha256, "1.27.1", "cpu", 0U};
        return result;
    }
};
}

int main()
{
    using namespace std::chrono_literals;
    using namespace infini::ocr;

    bgra_image crop{2U, 2U, 8U, {
        std::byte{0}, std::byte{0}, std::byte{255}, std::byte{255},
        std::byte{0}, std::byte{255}, std::byte{0}, std::byte{255},
        std::byte{255}, std::byte{0}, std::byte{0}, std::byte{255},
        std::byte{255}, std::byte{255}, std::byte{255}, std::byte{255},
    }};
    const bgra_image reduced = downscale_bgra(crop, 1U);
    require(reduced.width == 1U && reduced.height == 1U);
    const cloud_crop_encode_result encoded = encode_png(reduced, 1U * 1024U * 1024U);
    require(encoded.status == cloud_crop_encode_status::succeeded);
    require(encoded.bytes.size() > 8U);
    require(encoded.bytes[0] == std::byte{0x89} && encoded.bytes[1] == std::byte{'P'} &&
        encoded.bytes[2] == std::byte{'N'} && encoded.bytes[3] == std::byte{'G'});
    bgra_image masked = crop;
    const std::array masks{normalized_mask_rect{0.0, 0.0, 0.5, 1.0}};
    require(mask_bgra_regions(masked, masks));
    require(masked.pixels[0] == std::byte{0} &&
        masked.pixels[1] == std::byte{0} &&
        masked.pixels[2] == std::byte{0} &&
        masked.pixels[3] == std::byte{255});
    require(masked.pixels[4] == crop.pixels[4]);
    const std::array invalid_masks{normalized_mask_rect{0.9, 0.0, 0.2, 1.0}};
    require(!mask_bgra_regions(masked, invalid_masks));
    windows_media_ocr windows_ocr;
    const windows_media_ocr_result unavailable = windows_ocr.recognize(crop, "zz-ZZ");
    require(unavailable.status == windows_media_ocr_status::language_unavailable);

    rgba_image source{};
    source.width = 2;
    source.height = 1;
    source.pixels = {0, 0, 0, 255, 255, 255, 255, 0};
    preprocessing_options options{};
    options.alpha_cleanup = true;
    options.scale = 2U;
    grayscale_image cleaned = preprocess(source, options);
    require(cleaned.width == 4 && cleaned.height == 2);
    require(cleaned.pixels.front() == 0U);
    require(cleaned.pixels[2] == 255U);

    options.invert = true;
    grayscale_image inverted = preprocess(source, options);
    require(inverted.pixels.front() == 255U);
    require(inverted.pixels[2] == 0U);

    rgba_image colors{2, 1, {255, 0, 0, 255, 0, 0, 255, 255}};
    preprocessing_options isolation{};
    isolation.color_isolation = color_isolation_options{255, 0, 0, 10};
    grayscale_image isolated = preprocess(colors, isolation);
    require(isolated.pixels[0] == 255U);
    require(isolated.pixels[1] == 0U);

    rgba_image threshold_source{3, 3, {
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 0, 0, 0, 255, 255, 255, 255, 255,
        255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255, 255,
    }};
    preprocessing_options threshold_options{};
    threshold_options.adaptive_threshold = true;
    threshold_options.adaptive_radius = 1U;
    grayscale_image thresholded = preprocess(threshold_source, threshold_options);
    require(thresholded.pixels[4] == 0U);
    require(thresholded.pixels[0] == 255U);

    const preprocessing_parse_result no_steps = parse_preprocessing_pipeline("[]");
    require(no_steps.valid && !no_steps.enabled);
    const preprocessing_parse_result parsed_steps = parse_preprocessing_pipeline(
        "[\"grayscale\",\"contrast:1.5\",\"adaptive-threshold:8\","
        "\"color-isolation:#FF8000:24\",\"alpha-cleanup:32\",\"invert\"]");
    require(parsed_steps.valid && parsed_steps.enabled);
    require(parsed_steps.options.contrast == 1.5);
    require(parsed_steps.options.adaptive_threshold);
    require(parsed_steps.options.adaptive_radius == 8U);
    require(parsed_steps.options.color_isolation.has_value());
    require(parsed_steps.options.color_isolation->red == 255U);
    require(parsed_steps.options.color_isolation->green == 128U);
    require(parsed_steps.options.color_isolation->tolerance == 24U);
    require(parsed_steps.options.alpha_threshold == 32U);
    require(parsed_steps.options.invert);
    require(!parse_preprocessing_pipeline("[\"unknown\"]").valid);

    readback_ring ring(3U, 8U * 1024U * 1024U, 2U, 4U * 1024U * 1024U, 20ms);
    const auto first_ticket = ring.reserve(1024U);
    const auto second_ticket = ring.reserve(1024U);
    const auto third_ticket = ring.reserve(1024U);
    require(first_ticket && second_ticket && third_ticket);
    require(!ring.reserve(4U * 1024U * 1024U + 1U));
    ring.mark_fence_complete(*first_ticket);
    ring.mark_fence_complete(*second_ticket);
    ring.mark_fence_complete(*third_ticket);
    const auto now = readback_clock::time_point{};
    auto first_map = ring.try_map(*first_ticket, now);
    auto second_map = ring.try_map(*second_ticket, now);
    require(first_map && second_map);
    require(!ring.try_map(*third_ticket, now));
    require(first_map->within_hold_limit(now + 19ms));
    require(!first_map->within_hold_limit(now + 21ms));
    first_map.reset();
    require(ring.try_map(*third_ticket, now).has_value());

    onnx_session_pool pool(
        4U,
        256U * 1024U * 1024U,
        "1.27.1",
        [](const std::string& path) { return path == "present.onnx"; });
    model_descriptor missing{
        "missing", "1", "sha", "missing.onnx", {1, 3, 32, 128}, 20U, 1024U};
    require(pool.acquire(missing).status == session_acquire_status::model_missing);
    model_descriptor present{
        "tiny", "1", "sha", "present.onnx", {1, 3, 32, 128}, 20U, 1024U};
    auto session = pool.acquire(present);
    require(session.status == session_acquire_status::acquired);
    require(session.session.has_value());

    auto backend = std::make_shared<fake_backend>();
    ocr_engine engine(pool, backend);
    const ocr_engine_result recognized = engine.recognize(
        source,
        options,
        present,
        ocr_mode::fixed_region_recognizer);
    require(recognized.status == ocr_engine_status::succeeded);
    require(recognized.result.has_value());
    require(recognized.result->lines[0].orientation_degrees == 90);
    require(recognized.result->lines[0].vertical);
    require(recognized.result->model.execution_provider == "cpu");

    const ocr_engine_result absent = engine.recognize(
        source,
        options,
        missing,
        ocr_mode::fixed_region_recognizer);
    require(absent.status == ocr_engine_status::model_missing);

    return EXIT_SUCCESS;
}
