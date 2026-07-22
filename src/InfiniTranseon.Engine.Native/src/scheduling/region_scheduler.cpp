#include <infini/scheduling/region_scheduler.hpp>

#include <algorithm>
#include <array>
#include <chrono>
#include <stdexcept>
#include <tuple>

namespace infini::scheduling
{
namespace
{
constexpr std::array<work_priority, 15> priority_cycle{
    work_priority::p0, work_priority::p0, work_priority::p0, work_priority::p0,
    work_priority::p0, work_priority::p0, work_priority::p0, work_priority::p0,
    work_priority::p1, work_priority::p1, work_priority::p1, work_priority::p1,
    work_priority::p2, work_priority::p2,
    work_priority::p3};

constexpr std::size_t index_of(const work_priority priority) noexcept
{
    return static_cast<std::size_t>(priority);
}
}

void multi_rate_cadence::register_area(
    const work_key key,
    const area_mode mode,
    const std::chrono::milliseconds interval,
    const steady_clock::time_point first_due)
{
    if (interval <= std::chrono::milliseconds::zero())
        throw std::invalid_argument("area cadence must be positive");
    areas_[key] = {mode, interval, first_due};
}

bool multi_rate_cadence::remove_area(const work_key key)
{
    return areas_.erase(key) != 0U;
}

std::vector<work_key> multi_rate_cadence::take_due(const steady_clock::time_point now)
{
    std::vector<work_key> due;
    for (auto& [key, state] : areas_)
    {
        if (state.next_due > now) continue;
        due.push_back(key);
        do
        {
            state.next_due += state.interval;
        }
        while (state.next_due <= now);
    }
    return due;
}

region_scheduler::region_scheduler(const std::size_t maximum_queued_keys)
    : maximum_queued_keys_(maximum_queued_keys)
{
    if (maximum_queued_keys_ == 0U)
        throw std::invalid_argument("scheduler capacity must be positive");
}

schedule_submit_status region_scheduler::submit(recognition_work_item item)
{
    if (item.key != item.token.key || item.token.generation == 0U ||
        item.token.profile_revision == 0U || item.configured_interval < std::chrono::milliseconds::zero())
    {
        return schedule_submit_status::invalid;
    }
    std::scoped_lock lock(mutex_);
    const auto found = items_.find(item.key);
    if (found != items_.end())
    {
        found->second = std::move(item);
        return schedule_submit_status::replaced;
    }
    if (items_.size() >= maximum_queued_keys_)
        return schedule_submit_status::capacity_rejected;
    items_.emplace(item.key, std::move(item));
    return schedule_submit_status::accepted;
}

std::optional<recognition_work_item> region_scheduler::take_next(const steady_clock::time_point now)
{
    std::scoped_lock lock(mutex_);
    const std::optional<work_priority> selected_priority = choose_priority(now);
    if (!selected_priority) return std::nullopt;
    const std::size_t priority_index = index_of(*selected_priority);
    const std::uint64_t cursor = target_cursors_[priority_index];

    std::optional<std::uint64_t> wrapped_target;
    std::optional<std::uint64_t> next_target;
    for (const auto& [key, item] : items_)
    {
        if (effective_priority(item, now) != *selected_priority) continue;
        if (!wrapped_target || key.target_id < *wrapped_target) wrapped_target = key.target_id;
        if (key.target_id > cursor && (!next_target || key.target_id < *next_target))
            next_target = key.target_id;
    }
    const std::uint64_t target = next_target.value_or(*wrapped_target);

    auto selected = items_.end();
    for (auto item = items_.begin(); item != items_.end(); ++item)
    {
        if (item->first.target_id != target || effective_priority(item->second, now) != *selected_priority)
            continue;
        if (selected == items_.end() ||
            std::tie(item->second.deadline, item->first.area_id) <
                std::tie(selected->second.deadline, selected->first.area_id))
        {
            selected = item;
        }
    }

    recognition_work_item result = std::move(selected->second);
    items_.erase(selected);
    target_cursors_[priority_index] = target;
    if (*selected_priority == work_priority::p0) ++consecutive_p0_;
    else consecutive_p0_ = 0U;
    return result;
}

void region_scheduler::cancel_target(const std::uint64_t target_id)
{
    std::scoped_lock lock(mutex_);
    for (auto item = items_.begin(); item != items_.end();)
    {
        if (item->first.target_id == target_id) item = items_.erase(item);
        else ++item;
    }
}

std::size_t region_scheduler::queued_for_target(const std::uint64_t target_id) const
{
    std::scoped_lock lock(mutex_);
    return static_cast<std::size_t>(std::count_if(
        items_.begin(), items_.end(), [target_id](const auto& item)
        {
            return item.first.target_id == target_id;
        }));
}

std::size_t region_scheduler::size() const
{
    std::scoped_lock lock(mutex_);
    return items_.size();
}

work_priority region_scheduler::effective_priority(
    const recognition_work_item& item,
    const steady_clock::time_point now) const noexcept
{
    const auto aging_delay = std::max(item.configured_interval, std::chrono::milliseconds(500));
    const std::uint8_t priority = static_cast<std::uint8_t>(item.priority);
    if (priority > 0U && now - item.enqueued_at >= aging_delay)
        return static_cast<work_priority>(priority - 1U);
    return item.priority;
}

std::optional<work_priority> region_scheduler::choose_priority(const steady_clock::time_point now)
{
    std::array<bool, 4> available{};
    for (const auto& [key, item] : items_)
    {
        static_cast<void>(key);
        available[index_of(effective_priority(item, now))] = true;
    }
    if (!available[0] && !available[1] && !available[2] && !available[3]) return std::nullopt;

    if (consecutive_p0_ >= 8U)
    {
        for (std::size_t index = 1; index < available.size(); ++index)
        {
            if (available[index]) return static_cast<work_priority>(index);
        }
    }

    for (std::size_t attempts = 0; attempts < priority_cycle.size(); ++attempts)
    {
        const work_priority priority = priority_cycle[priority_cycle_index_];
        priority_cycle_index_ = (priority_cycle_index_ + 1U) % priority_cycle.size();
        if (available[index_of(priority)]) return priority;
    }
    for (std::size_t index = 0; index < available.size(); ++index)
    {
        if (available[index]) return static_cast<work_priority>(index);
    }
    return std::nullopt;
}
}
