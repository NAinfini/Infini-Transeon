#pragma once

#ifdef _WIN32

#include "infini/capture/frame_lease.hpp"

#include <d3d11.h>
#include <wrl/client.h>

#include <chrono>
#include <condition_variable>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <functional>
#include <mutex>
#include <thread>

namespace infini::imaging {

enum class device_submission_status : std::uint8_t {
    completed,
    job_failed,
    device_lost,
    fence_timeout,
    cancelled,
};

struct device_submission_result final {
    device_submission_status status{device_submission_status::cancelled};
    std::uint64_t device_epoch{};
    HRESULT error{S_OK};
};

class device_runtime final {
public:
    using gpu_job = std::function<HRESULT(ID3D11DeviceContext*)>;
    using completion_callback = std::function<void(device_submission_result)>;

    device_runtime(
        ID3D11Device* device,
        std::uint64_t device_epoch,
        std::size_t queue_capacity,
        std::chrono::milliseconds fence_timeout);
    ~device_runtime();
    device_runtime(const device_runtime&) = delete;
    device_runtime& operator=(const device_runtime&) = delete;

    [[nodiscard]] bool enqueue(
        capture::gpu_use_ticket ticket,
        gpu_job job,
        completion_callback completion);
    void stop() noexcept;
    [[nodiscard]] std::size_t queued() const noexcept;
    [[nodiscard]] bool accepting() const noexcept;

private:
    struct submission final {
        capture::gpu_use_ticket ticket;
        gpu_job job;
        completion_callback completion;
    };

    void run() noexcept;
    void execute(submission work) noexcept;
    void complete(submission& work, device_submission_status status, HRESULT error) noexcept;

    Microsoft::WRL::ComPtr<ID3D11Device> device_;
    Microsoft::WRL::ComPtr<ID3D11DeviceContext> context_;
    std::uint64_t device_epoch_{};
    std::size_t queue_capacity_{};
    std::chrono::milliseconds fence_timeout_{};
    mutable std::mutex gate_;
    std::condition_variable changed_;
    std::deque<submission> queue_;
    bool stopping_{};
    std::thread worker_;
};

} // namespace infini::imaging

#endif
