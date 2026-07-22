#pragma once

#include <condition_variable>
#include <cstdint>
#include <functional>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <thread>
#include <utility>

namespace infini::scheduling
{
enum class latest_worker_submit_status
{
    accepted,
    replaced,
    stopped,
};

struct latest_worker_statistics final
{
    std::uint64_t accepted{};
    std::uint64_t replaced{};
    std::uint64_t processed{};
    std::uint64_t failed{};
    std::uint64_t discarded_on_stop{};
};

template<typename Value>
class latest_worker final
{
public:
    using processor = std::function<void(Value)>;

    explicit latest_worker(processor process) : process_(std::move(process))
    {
        if (!process_) throw std::invalid_argument("latest worker processor is required");
    }

    ~latest_worker() { stop(); }
    latest_worker(const latest_worker&) = delete;
    latest_worker& operator=(const latest_worker&) = delete;

    [[nodiscard]] bool start()
    {
        std::scoped_lock lock(gate_);
        if (started_ || stopped_) return false;
        started_ = true;
        thread_ = std::thread([this] { run(); });
        changed_.notify_one();
        return true;
    }

    [[nodiscard]] latest_worker_submit_status submit(Value value)
    {
        std::scoped_lock lock(gate_);
        if (stopped_) return latest_worker_submit_status::stopped;
        if (pending_.has_value())
        {
            pending_ = std::move(value);
            ++statistics_.replaced;
            changed_.notify_one();
            return latest_worker_submit_status::replaced;
        }
        pending_ = std::move(value);
        ++statistics_.accepted;
        changed_.notify_one();
        return latest_worker_submit_status::accepted;
    }

    void stop() noexcept
    {
        {
            std::scoped_lock lock(gate_);
            if (stopped_) return;
            stopped_ = true;
            if (pending_.has_value())
            {
                pending_.reset();
                ++statistics_.discarded_on_stop;
            }
        }
        changed_.notify_all();
        if (thread_.joinable()) thread_.join();
    }

    [[nodiscard]] latest_worker_statistics statistics() const noexcept
    {
        std::scoped_lock lock(gate_);
        return statistics_;
    }

private:
    void run() noexcept
    {
        while (true)
        {
            std::optional<Value> next;
            {
                std::unique_lock lock(gate_);
                changed_.wait(lock, [this]
                {
                    return stopped_ || pending_.has_value();
                });
                if (stopped_) return;
                next = std::move(pending_);
                pending_.reset();
            }
            try
            {
                process_(std::move(*next));
                std::scoped_lock lock(gate_);
                ++statistics_.processed;
            }
            catch (...)
            {
                std::scoped_lock lock(gate_);
                ++statistics_.failed;
            }
        }
    }

    processor process_;
    mutable std::mutex gate_;
    std::condition_variable changed_;
    std::optional<Value> pending_;
    std::thread thread_;
    latest_worker_statistics statistics_{};
    bool started_{};
    bool stopped_{};
};
}
