#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <map>
#include <mutex>
#include <optional>
#include <vector>

#include <infini/scheduling/work_types.hpp>

namespace infini::scheduling
{
enum class schedule_submit_status
{
    accepted,
    replaced,
    capacity_rejected,
    invalid,
};

class multi_rate_cadence final
{
public:
    void register_area(
        work_key key,
        area_mode mode,
        std::chrono::milliseconds interval,
        steady_clock::time_point first_due);
    bool remove_area(work_key key);
    [[nodiscard]] std::vector<work_key> take_due(steady_clock::time_point now);

private:
    struct cadence_state
    {
        area_mode mode{area_mode::user_region};
        std::chrono::milliseconds interval{};
        steady_clock::time_point next_due{};
    };
    std::map<work_key, cadence_state> areas_{};
};

class region_scheduler final
{
public:
    explicit region_scheduler(std::size_t maximum_queued_keys);

    schedule_submit_status submit(recognition_work_item item);
    std::optional<recognition_work_item> take_next(steady_clock::time_point now);
    void cancel_target(std::uint64_t target_id);
    [[nodiscard]] std::size_t queued_for_target(std::uint64_t target_id) const;
    [[nodiscard]] std::size_t size() const;

private:
    [[nodiscard]] work_priority effective_priority(
        const recognition_work_item& item,
        steady_clock::time_point now) const noexcept;
    [[nodiscard]] std::optional<work_priority> choose_priority(steady_clock::time_point now);

    std::size_t maximum_queued_keys_{};
    mutable std::mutex mutex_{};
    std::map<work_key, recognition_work_item> items_{};
    std::array<std::uint64_t, 4> target_cursors_{};
    std::size_t priority_cycle_index_{};
    std::size_t consecutive_p0_{};
};
}
