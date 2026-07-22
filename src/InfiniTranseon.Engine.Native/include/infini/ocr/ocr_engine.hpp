#pragma once

#include <chrono>
#include <cstdint>
#include <memory>
#include <optional>
#include <string>
#include <vector>

#include <infini/imaging/coordinate_mapper.hpp>
#include <infini/ocr/onnx_session_pool.hpp>
#include <infini/ocr/preprocessing_pipeline.hpp>

namespace infini::ocr
{
enum class ocr_mode
{
    fixed_region_recognizer,
    detector_and_recognizer,
};

struct ocr_line
{
    std::string text{};
    imaging::normalized_rect bounds{};
    double confidence{};
    std::int32_t orientation_degrees{};
    bool vertical{};
};

struct ocr_model_metadata
{
    std::string model_id{};
    std::string model_version{};
    std::string model_sha256{};
    std::string runtime_version{};
    std::string execution_provider{};
    std::uint64_t adapter_luid{};
};

struct ocr_latency
{
    std::chrono::microseconds preprocessing{};
    std::chrono::microseconds inference{};
    std::chrono::microseconds copy{};
    std::chrono::microseconds fence_wait{};
    std::chrono::microseconds map{};
    std::chrono::microseconds tensorization{};
};

struct ocr_result
{
    std::vector<ocr_line> lines{};
    ocr_model_metadata model{};
    ocr_latency latency{};
};

class local_ocr_backend
{
public:
    virtual ~local_ocr_backend() = default;
    virtual ocr_result recognize(
        const grayscale_image& image,
        const model_descriptor& model,
        ocr_mode mode) = 0;
};

enum class ocr_engine_status
{
    succeeded,
    invalid_input,
    model_missing,
    runtime_incompatible,
    capacity_rejected,
    backend_failed,
};

struct ocr_engine_result
{
    ocr_engine_status status{ocr_engine_status::invalid_input};
    std::optional<ocr_result> result{};
    std::string error_code{};
};

class ocr_engine final
{
public:
    ocr_engine(onnx_session_pool& sessions, std::shared_ptr<local_ocr_backend> backend);

    ocr_engine_result recognize(
        const rgba_image& source,
        const preprocessing_options& preprocessing,
        const model_descriptor& model,
        ocr_mode mode);

private:
    onnx_session_pool& sessions_;
    std::shared_ptr<local_ocr_backend> backend_;
};
}
