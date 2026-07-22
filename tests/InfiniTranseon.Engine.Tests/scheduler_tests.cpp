#include <array>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdlib>
#include <iostream>
#include <memory>
#include <mutex>
#include <string>
#include <thread>
#include <vector>

#include <infini/imaging/change_detector.hpp>
#include <infini/imaging/text_box_tracker.hpp>
#include <infini/scheduling/generation_registry.hpp>
#include <infini/scheduling/latest_queue.hpp>
#include <infini/scheduling/latest_worker.hpp>
#include <infini/scheduling/region_scheduler.hpp>

namespace
{
void require_at(const bool condition, const int line)
{
    if (!condition)
    {
        std::cerr << "scheduler test assertion failed at line " << line << '\n';
        std::abort();
    }
}
}

#define require(...) require_at((__VA_ARGS__), __LINE__)

int main()
{
    using namespace std::chrono_literals;
    using namespace infini::imaging;
    using namespace infini::scheduling;
    const auto start = steady_clock::time_point{};

    require(detection_plane_size({3840, 2160}) == image_size{1920, 1080});
    require(detection_plane_size({2160, 3840}) == image_size{1080, 1920});
    const std::array<std::uint64_t, 4> static_tiles{1, 2, 3, 4};
    const std::array<std::uint64_t, 4> moving_tiles{9, 8, 7, 6};
    require(changed_tiles(static_tiles, static_tiles, 0).empty());
    require(changed_tiles(static_tiles, moving_tiles, 0).size() == 4U);

    constexpr std::uint32_t test_width = 3840U;
    constexpr std::uint32_t test_height = 2160U;
    constexpr std::uint32_t test_stride = test_width * 4U;
    std::vector<std::uint8_t> white_frame(
        static_cast<std::size_t>(test_stride) * test_height, 255U);
    const change_signature white_signature = make_bgra_change_signature(
        white_frame, test_width, test_height, test_stride);
    require(!meaningfully_changed(white_signature, white_signature));

    std::vector<std::uint8_t> text_frame = white_frame;
    for (std::uint32_t y = 1000U; y < 1032U; ++y)
    {
        for (std::uint32_t x = 1800U; x < 1920U; ++x)
        {
            const std::size_t offset = static_cast<std::size_t>(y) * test_stride + x * 4U;
            text_frame[offset] = 0U;
            text_frame[offset + 1U] = 0U;
            text_frame[offset + 2U] = 0U;
        }
    }
    const change_signature text_signature = make_bgra_change_signature(
        text_frame, test_width, test_height, test_stride);
    require(meaningfully_changed(white_signature, text_signature));

    std::vector<std::uint8_t> noisy_frame = white_frame;
    for (std::size_t offset = 0U; offset < noisy_frame.size(); offset += 4U)
    {
        noisy_frame[offset] = 252U;
        noisy_frame[offset + 1U] = 252U;
        noisy_frame[offset + 2U] = 252U;
    }
    const change_signature noisy_signature = make_bgra_change_signature(
        noisy_frame, test_width, test_height, test_stride);
    require(!meaningfully_changed(white_signature, noisy_signature));
    const remaining_mask mask = compute_remaining_area_mask(
        {0.0, 0.0, 1.0, 1.0},
        std::array<normalized_rect, 1>{{{0.25, 0.25, 0.5, 0.5}}},
        16U);
    require(mask.status == remaining_mask_status::ok);
    require(mask.fragments.size() == 4U);
    double remaining_area = 0.0;
    for (const normalized_rect& fragment : mask.fragments)
        remaining_area += fragment.width * fragment.height;
    require(std::abs(remaining_area - 0.75) < 0.000001);

    const std::array<detection_candidate, 2> candidates{
        detection_candidate{1U, {0.3, 0.3, 0.1, 0.1}, 1U},
        detection_candidate{2U, {0.8, 0.8, 0.1, 0.1}, 1U}};
    const auto deduplicated = exclude_explicit_region_overlaps(
        candidates,
        std::array<normalized_rect, 1>{{{0.25, 0.25, 0.5, 0.5}}},
        0.5);
    require(deduplicated.size() == 1U);
    require(deduplicated[0].candidate_id == 2U);

    latest_queue<int, int> latest(2U);
    require(latest.push(1, 10).status == latest_push_status::accepted);
    const auto replaced = latest.push(1, 11);
    require(replaced.status == latest_push_status::replaced);
    require(replaced.displaced == 10);
    require(latest.push(2, 20).status == latest_push_status::accepted);
    require(latest.push(3, 30).status == latest_push_status::capacity_rejected);
    require(latest.take(1) == 11);

    std::mutex worker_gate;
    std::condition_variable worker_changed;
    std::vector<int> processed_values;
    {
        latest_worker<int> worker([&](const int value)
        {
            {
                std::scoped_lock lock(worker_gate);
                processed_values.push_back(value);
            }
            worker_changed.notify_all();
        });
        require(worker.submit(10) == latest_worker_submit_status::accepted);
        require(worker.submit(11) == latest_worker_submit_status::replaced);
        require(worker.start());
        std::unique_lock lock(worker_gate);
        require(worker_changed.wait_for(lock, 2s, [&]
        {
            return processed_values.size() == 1U;
        }));
        require(processed_values[0] == 11);
        lock.unlock();
        worker.stop();
        require(worker.submit(12) == latest_worker_submit_status::stopped);
        const latest_worker_statistics statistics = worker.statistics();
        require(statistics.accepted == 1U);
        require(statistics.replaced == 1U);
        require(statistics.processed == 1U);
    }

    generation_registry generations;
    const work_key generation_key{1U, 100U};
    const generation_token first = generations.activate(generation_key, 7U);
    require(generations.is_current(first));
    const generation_token second = generations.rollover(generation_key, 8U);
    require(!generations.is_current(first));
    require(generations.is_current(second));
    generations.cancel(generation_key);
    require(!generations.is_current(second));

    multi_rate_cadence cadence;
    cadence.register_area({1U, 1U}, area_mode::user_region, 100ms, start);
    cadence.register_area({1U, 2U}, area_mode::remaining_area, 1s, start);
    require(cadence.take_due(start).size() == 2U);
    const auto early_due = cadence.take_due(start + 200ms);
    require(early_due.size() == 1U && early_due[0] == work_key{1U, 1U});
    const auto one_second_due = cadence.take_due(start + 1s);
    require(one_second_due.size() == 2U);

    region_scheduler scheduler(64U);
    for (std::uint64_t index = 0; index < 50U; ++index)
    {
        recognition_work_item item{};
        item.key = {index % 2U, index};
        item.token = {item.key, 1U, 1U};
        item.priority = index == 49U ? work_priority::p1 : work_priority::p0;
        item.enqueued_at = start;
        item.deadline = start + std::chrono::milliseconds(100 + index);
        item.configured_interval = 100ms;
        require(scheduler.submit(item) != schedule_submit_status::capacity_rejected);
    }
    recognition_work_item replacement{};
    replacement.key = {0U, 0U};
    replacement.token = {replacement.key, 2U, 1U};
    replacement.priority = work_priority::p0;
    replacement.enqueued_at = start;
    replacement.deadline = start + 1ms;
    replacement.configured_interval = 100ms;
    require(scheduler.submit(replacement) == schedule_submit_status::replaced);

    std::size_t consecutive_p0 = 0U;
    bool observed_p1 = false;
    for (std::size_t count = 0; count < 12U; ++count)
    {
        const auto next = scheduler.take_next(start + 100ms);
        require(next.has_value());
        if (next->priority == work_priority::p0)
        {
            ++consecutive_p0;
            require(consecutive_p0 <= 8U);
        }
        else
        {
            observed_p1 = true;
            consecutive_p0 = 0U;
        }
    }
    require(observed_p1);
    scheduler.cancel_target(1U);
    require(scheduler.queued_for_target(1U) == 0U);

    text_box_tracker tracker({2U, 100ms, 500ms, 5s});
    const text_box box{1U, {0.1, 0.7, 0.8, 0.2}};
    const track_observation first_text = tracker.observe(box, "Hel", start, 1U);
    require(!first_text.source_event_id.has_value());
    const track_observation stable_text = tracker.observe(box, "Hel", start + 120ms, 1U);
    require(stable_text.source_event_id.has_value());
    const std::uint64_t event_id = *stable_text.source_event_id;
    tracker.observe(box, "Hello", start + 200ms, 1U);
    const track_observation continued = tracker.observe(box, "Hello", start + 320ms, 1U);
    require(continued.source_event_id == event_id);
    require(continued.reused_event);

    tracker.observe(box, "Unrelated", start + 400ms, 1U);
    const track_observation replacement_text = tracker.observe(box, "Unrelated", start + 520ms, 1U);
    require(replacement_text.source_event_id.has_value());
    require(replacement_text.source_event_id != event_id);
    tracker.clear(box, start + 600ms);
    tracker.observe(box, "Unrelated", start + 700ms, 1U);
    const track_observation reappeared = tracker.observe(box, "Unrelated", start + 820ms, 1U);
    require(reappeared.source_event_id.has_value());
    require(reappeared.source_event_id != replacement_text.source_event_id);

    const track_observation forced = tracker.observe(
        {2U, {0.1, 0.1, 0.5, 0.1}}, "A", start, 1U);
    require(!forced.stable);
    const track_observation forced_progress = tracker.observe(
        {2U, {0.1, 0.1, 0.5, 0.1}}, "AB", start + 600ms, 1U);
    require(forced_progress.stable);
    require(forced_progress.forced_progress);

    text_box_tracker crowded_tracker({2U, 100ms, 500ms, 5s});
    for (std::uint64_t index = 0; index < 36U; ++index)
    {
        const double x = static_cast<double>(index % 6U) * 0.007;
        const double y = static_cast<double>(index / 6U) * 0.007;
        const track_observation item = crowded_tracker.observe(
            {100U + index, {x, y, 0.005, 0.005}},
            std::to_string(index),
            start,
            1U);
        require(index <= 32U ? !item.uncertain_association : item.uncertain_association);
    }
    const track_observation uncertain = crowded_tracker.observe(
        {999U, {0.0, 0.0, 0.05, 0.05}}, "crowded", start, 1U);
    require(uncertain.uncertain_association);

    return EXIT_SUCCESS;
}
