#pragma once

#include <array>
#include <chrono>
#include <cstddef>
#include <cstdint>
#include <map>
#include <optional>
#include <string>
#include <utility>
#include <vector>

#include <infini/imaging/coordinate_mapper.hpp>
#include <infini/scheduling/work_types.hpp>

namespace infini::imaging
{
struct text_box
{
    std::uint64_t candidate_id{};
    normalized_rect bounds{};
};

struct text_tracker_config
{
    std::size_t stable_frame_count{2U};
    std::chrono::milliseconds minimum_delay{100};
    std::chrono::milliseconds maximum_wait{500};
    std::chrono::milliseconds continuation_window{5000};
};

struct track_observation
{
    std::uint64_t track_id{};
    bool stable{};
    bool forced_progress{};
    bool uncertain_association{};
    bool reused_event{};
    std::optional<std::uint64_t> source_event_id{};
};

class text_box_tracker final
{
public:
    explicit text_box_tracker(text_tracker_config config);

    track_observation observe(
        text_box box,
        std::string text,
        scheduling::steady_clock::time_point observed_at,
        std::uint64_t generation);
    void clear(text_box box, scheduling::steady_clock::time_point observed_at);

private:
    struct track_state
    {
        std::uint64_t track_id{};
        std::uint64_t candidate_id{};
        std::uint64_t generation{};
        normalized_rect bounds{};
        std::string pending_text{};
        std::string stable_text{};
        std::size_t consecutive_frames{};
        scheduling::steady_clock::time_point pending_since{};
        scheduling::steady_clock::time_point sequence_started{};
        scheduling::steady_clock::time_point last_event_at{};
        std::optional<std::uint64_t> source_event_id{};
        bool has_stable_text{};
    };

    struct cell_bucket
    {
        bool subdivided{};
        std::vector<std::uint64_t> coarse{};
        std::array<std::vector<std::uint64_t>, 4> fine{};
    };

    [[nodiscard]] std::pair<std::optional<std::uint64_t>, bool> associate(const text_box& box) const;
    std::uint64_t create_track(const text_box& box, std::uint64_t generation);
    void remove_track(std::uint64_t track_id);
    void insert_into_grid(std::uint64_t track_id, const normalized_rect& bounds);
    void remove_from_grid(std::uint64_t track_id, const normalized_rect& bounds);

    text_tracker_config config_{};
    std::uint64_t next_track_id_{1U};
    std::uint64_t next_event_id_{1U};
    std::map<std::uint64_t, track_state> tracks_{};
    std::map<std::uint64_t, std::uint64_t> candidate_tracks_{};
    std::map<std::pair<int, int>, cell_bucket> grid_{};
};
}
