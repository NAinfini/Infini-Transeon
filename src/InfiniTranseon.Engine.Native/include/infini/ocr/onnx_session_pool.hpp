#pragma once

#include <cstddef>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <string>
#include <vector>

namespace infini::ocr
{
struct model_descriptor
{
    std::string model_id{};
    std::string version{};
    std::string sha256{};
    std::string path{};
    std::vector<std::int64_t> input_shape{};
    std::uint32_t opset{};
    std::size_t workspace_bytes{};
};

enum class session_acquire_status
{
    acquired,
    model_missing,
    runtime_incompatible,
    invalid_model,
    capacity_rejected,
};

class onnx_session_pool;

class onnx_session_lease final
{
public:
    onnx_session_lease() = default;
    onnx_session_lease(const onnx_session_lease&) = delete;
    onnx_session_lease& operator=(const onnx_session_lease&) = delete;
    onnx_session_lease(onnx_session_lease&& other) noexcept;
    onnx_session_lease& operator=(onnx_session_lease&& other) noexcept;
    ~onnx_session_lease();

    [[nodiscard]] const model_descriptor& model() const noexcept { return model_; }

private:
    friend class onnx_session_pool;
    onnx_session_lease(onnx_session_pool* owner, model_descriptor model) noexcept;
    void release() noexcept;

    onnx_session_pool* owner_{};
    model_descriptor model_{};
};

struct session_acquire_result
{
    session_acquire_status status{session_acquire_status::invalid_model};
    std::optional<onnx_session_lease> session{};
};

class onnx_session_pool final
{
public:
    using model_exists = std::function<bool(const std::string&)>;

    onnx_session_pool(
        std::size_t maximum_sessions,
        std::size_t maximum_workspace_bytes,
        std::string runtime_version,
        model_exists exists);

    session_acquire_result acquire(const model_descriptor& model);
    [[nodiscard]] const std::string& runtime_version() const noexcept { return runtime_version_; }

private:
    friend class onnx_session_lease;
    void release(std::size_t workspace_bytes) noexcept;

    std::size_t maximum_sessions_{};
    std::size_t maximum_workspace_bytes_{};
    std::string runtime_version_{};
    model_exists exists_{};
    std::mutex mutex_{};
    std::size_t active_sessions_{};
    std::size_t committed_workspace_bytes_{};
};
}
