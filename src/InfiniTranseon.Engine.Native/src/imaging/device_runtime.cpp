#include "infini/imaging/device_runtime.hpp"

#ifdef _WIN32

#include <stdexcept>
#include <utility>
#include <windows.h>

namespace infini::imaging {

device_runtime::device_runtime(
    ID3D11Device* device,
    const std::uint64_t device_epoch,
    const std::size_t queue_capacity,
    const std::chrono::milliseconds fence_timeout)
    : device_(device),
      device_epoch_(device_epoch),
      queue_capacity_(queue_capacity),
      fence_timeout_(fence_timeout) {
    if (device == nullptr || device_epoch == 0U || queue_capacity == 0U || fence_timeout.count() <= 0)
        throw std::invalid_argument("invalid device runtime configuration");
    device_->GetImmediateContext(&context_);
    if (!context_) throw std::runtime_error("D3D11 immediate context is unavailable");
    worker_ = std::thread([this] { run(); });
}

device_runtime::~device_runtime() { stop(); }

bool device_runtime::enqueue(
    capture::gpu_use_ticket ticket,
    gpu_job job,
    completion_callback completion) {
    if (!ticket.valid() || !job) return false;
    {
        std::scoped_lock lock(gate_);
        if (stopping_ || queue_.size() >= queue_capacity_) return false;
        queue_.push_back({std::move(ticket), std::move(job), std::move(completion)});
    }
    changed_.notify_one();
    return true;
}

void device_runtime::stop() noexcept {
    {
        std::scoped_lock lock(gate_);
        if (stopping_) {
            if (!worker_.joinable()) return;
        } else {
            stopping_ = true;
        }
    }
    changed_.notify_one();
    if (worker_.joinable() && worker_.get_id() != std::this_thread::get_id()) worker_.join();
}

std::size_t device_runtime::queued() const noexcept {
    std::scoped_lock lock(gate_);
    return queue_.size();
}

bool device_runtime::accepting() const noexcept {
    std::scoped_lock lock(gate_);
    return !stopping_;
}

void device_runtime::run() noexcept {
    for (;;) {
        submission work;
        {
            std::unique_lock lock(gate_);
            changed_.wait(lock, [this] { return stopping_ || !queue_.empty(); });
            if (stopping_) {
                while (!queue_.empty()) {
                    auto cancelled = std::move(queue_.front());
                    queue_.pop_front();
                    lock.unlock();
                    complete(cancelled, device_submission_status::cancelled, HRESULT_FROM_WIN32(ERROR_CANCELLED));
                    lock.lock();
                }
                break;
            }
            work = std::move(queue_.front());
            queue_.pop_front();
        }
        execute(std::move(work));
    }
    context_->ClearState();
    context_->Flush();
}

void device_runtime::execute(submission work) noexcept {
    D3D11_QUERY_DESC query_description{D3D11_QUERY_EVENT, 0U};
    Microsoft::WRL::ComPtr<ID3D11Query> fence;
    HRESULT result = device_->CreateQuery(&query_description, &fence);
    if (FAILED(result)) {
        complete(work, device_submission_status::job_failed, result);
        return;
    }
    if (!work.ticket.submit()) {
        complete(work, device_submission_status::cancelled, HRESULT_FROM_WIN32(ERROR_CANCELLED));
        return;
    }
    try { result = work.job(context_.Get()); }
    catch (...) { result = E_FAIL; }
    context_->End(fence.Get());
    context_->Flush();

    const auto deadline = std::chrono::steady_clock::now() + fence_timeout_;
    HRESULT fence_result = S_FALSE;
    while (std::chrono::steady_clock::now() < deadline) {
        fence_result = context_->GetData(fence.Get(), nullptr, 0U, 0U);
        if (fence_result == S_OK || FAILED(fence_result)) break;
        Sleep(1U);
    }
    if (fence_result != S_OK) {
        const HRESULT removed = device_->GetDeviceRemovedReason();
        complete(work,
            FAILED(removed) ? device_submission_status::device_lost : device_submission_status::fence_timeout,
            FAILED(removed) ? removed : DXGI_ERROR_WAIT_TIMEOUT);
        return;
    }
    complete(work, FAILED(result) ? device_submission_status::job_failed : device_submission_status::completed,
        result);
}

void device_runtime::complete(
    submission& work,
    const device_submission_status status,
    const HRESULT error) noexcept {
    if (work.ticket.valid()) {
        if (status == device_submission_status::cancelled) static_cast<void>(work.ticket.cancel());
        else static_cast<void>(work.ticket.complete());
    }
    if (work.completion) {
        try { work.completion({status, device_epoch_, error}); } catch (...) { }
    }
}

} // namespace infini::imaging

#endif
