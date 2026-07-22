#include <infini/ocr/ocr_engine.hpp>

#include <chrono>
#include <exception>
#include <stdexcept>
#include <utility>

namespace infini::ocr
{
ocr_engine::ocr_engine(
    onnx_session_pool& sessions,
    std::shared_ptr<local_ocr_backend> backend)
    : sessions_(sessions), backend_(std::move(backend))
{
    if (!backend_) throw std::invalid_argument("local OCR backend is required");
}

ocr_engine_result ocr_engine::recognize(
    const rgba_image& source,
    const preprocessing_options& preprocessing,
    const model_descriptor& model,
    const ocr_mode mode)
{
    session_acquire_result session = sessions_.acquire(model);
    switch (session.status)
    {
    case session_acquire_status::model_missing:
        return {ocr_engine_status::model_missing, std::nullopt, "ocr.model.missing"};
    case session_acquire_status::runtime_incompatible:
        return {ocr_engine_status::runtime_incompatible, std::nullopt, "ocr.runtime.incompatible"};
    case session_acquire_status::capacity_rejected:
        return {ocr_engine_status::capacity_rejected, std::nullopt, "ocr.capacity.sessionPool"};
    case session_acquire_status::invalid_model:
        return {ocr_engine_status::invalid_input, std::nullopt, "ocr.model.invalid"};
    case session_acquire_status::acquired:
        break;
    }

    try
    {
        const auto preprocessing_started = std::chrono::steady_clock::now();
        grayscale_image image = preprocess(source, preprocessing);
        const auto inference_started = std::chrono::steady_clock::now();
        ocr_result result = backend_->recognize(image, session.session->model(), mode);
        const auto completed = std::chrono::steady_clock::now();
        result.latency.preprocessing = std::chrono::duration_cast<std::chrono::microseconds>(
            inference_started - preprocessing_started);
        result.latency.inference = std::chrono::duration_cast<std::chrono::microseconds>(
            completed - inference_started);
        if (result.model.model_id.empty() || result.model.model_version.empty() ||
            result.model.model_sha256.empty() || result.model.runtime_version.empty() ||
            result.model.execution_provider.empty())
        {
            return {ocr_engine_status::backend_failed, std::nullopt, "ocr.backend.metadataMissing"};
        }
        return {ocr_engine_status::succeeded, std::move(result), {}};
    }
    catch (const std::invalid_argument&)
    {
        return {ocr_engine_status::invalid_input, std::nullopt, "ocr.preprocessing.invalid"};
    }
    catch (const std::exception&)
    {
        return {ocr_engine_status::backend_failed, std::nullopt, "ocr.backend.failed"};
    }
}
}
