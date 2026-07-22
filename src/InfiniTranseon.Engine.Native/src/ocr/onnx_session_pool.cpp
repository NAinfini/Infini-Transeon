#include <infini/ocr/onnx_session_pool.hpp>

#include <algorithm>
#include <stdexcept>
#include <utility>

namespace infini::ocr
{
namespace
{
constexpr const char* supported_runtime = "1.27.1";
constexpr std::uint32_t minimum_opset = 11U;
constexpr std::uint32_t maximum_opset = 23U;
}

onnx_session_lease::onnx_session_lease(
    onnx_session_pool* const owner,
    model_descriptor model) noexcept
    : owner_(owner), model_(std::move(model))
{
}

onnx_session_lease::onnx_session_lease(onnx_session_lease&& other) noexcept
    : owner_(std::exchange(other.owner_, nullptr)), model_(std::move(other.model_))
{
}

onnx_session_lease& onnx_session_lease::operator=(onnx_session_lease&& other) noexcept
{
    if (this != &other)
    {
        release();
        owner_ = std::exchange(other.owner_, nullptr);
        model_ = std::move(other.model_);
    }
    return *this;
}

onnx_session_lease::~onnx_session_lease()
{
    release();
}

void onnx_session_lease::release() noexcept
{
    if (owner_ != nullptr)
    {
        owner_->release(model_.workspace_bytes);
        owner_ = nullptr;
    }
}

onnx_session_pool::onnx_session_pool(
    const std::size_t maximum_sessions,
    const std::size_t maximum_workspace_bytes,
    std::string runtime_version,
    model_exists exists)
    : maximum_sessions_(maximum_sessions),
      maximum_workspace_bytes_(maximum_workspace_bytes),
      runtime_version_(std::move(runtime_version)),
      exists_(std::move(exists))
{
    if (maximum_sessions_ == 0U || maximum_workspace_bytes_ == 0U ||
        runtime_version_.empty() || !exists_)
    {
        throw std::invalid_argument("ONNX session pool configuration is invalid");
    }
}

session_acquire_result onnx_session_pool::acquire(const model_descriptor& model)
{
    const bool invalid = model.model_id.empty() || model.version.empty() || model.sha256.empty() ||
        model.path.empty() || model.input_shape.empty() ||
        std::any_of(model.input_shape.begin(), model.input_shape.end(), [](const auto value) { return value <= 0; }) ||
        model.opset < minimum_opset || model.opset > maximum_opset || model.workspace_bytes == 0U;
    if (invalid) return {session_acquire_status::invalid_model, std::nullopt};
    if (runtime_version_ != supported_runtime)
        return {session_acquire_status::runtime_incompatible, std::nullopt};
    if (!exists_(model.path)) return {session_acquire_status::model_missing, std::nullopt};

    std::scoped_lock lock(mutex_);
    if (active_sessions_ >= maximum_sessions_ ||
        model.workspace_bytes > maximum_workspace_bytes_ - committed_workspace_bytes_)
    {
        return {session_acquire_status::capacity_rejected, std::nullopt};
    }
    ++active_sessions_;
    committed_workspace_bytes_ += model.workspace_bytes;
    return {
        session_acquire_status::acquired,
        onnx_session_lease(this, model)};
}

void onnx_session_pool::release(const std::size_t workspace_bytes) noexcept
{
    std::scoped_lock lock(mutex_);
    if (active_sessions_ > 0U) --active_sessions_;
    committed_workspace_bytes_ = workspace_bytes <= committed_workspace_bytes_
        ? committed_workspace_bytes_ - workspace_bytes
        : 0U;
}
}
