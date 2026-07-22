#include <infini/imaging/text_box_tracker.hpp>

#include <algorithm>
#include <cctype>
#include <cmath>
#include <stdexcept>
#include <string_view>

namespace infini::imaging
{
namespace
{
constexpr std::size_t maximum_cell_scan = 32U;
constexpr std::size_t maximum_scored_candidates = 8U;
constexpr int grid_dimension = 10;

bool valid(const normalized_rect& bounds) noexcept
{
    return std::isfinite(bounds.x) && std::isfinite(bounds.y) &&
        std::isfinite(bounds.width) && std::isfinite(bounds.height) &&
        bounds.x >= 0.0 && bounds.y >= 0.0 && bounds.width > 0.0 &&
        bounds.height > 0.0 && bounds.x + bounds.width <= 1.0 &&
        bounds.y + bounds.height <= 1.0;
}

std::pair<int, int> cell_for(const normalized_rect& bounds) noexcept
{
    const double center_x = bounds.x + bounds.width / 2.0;
    const double center_y = bounds.y + bounds.height / 2.0;
    return {
        std::clamp(static_cast<int>(center_x * grid_dimension), 0, grid_dimension - 1),
        std::clamp(static_cast<int>(center_y * grid_dimension), 0, grid_dimension - 1)};
}

std::size_t fine_cell_for(const normalized_rect& bounds) noexcept
{
    const double scaled_x = (bounds.x + bounds.width / 2.0) * grid_dimension;
    const double scaled_y = (bounds.y + bounds.height / 2.0) * grid_dimension;
    const std::size_t x = scaled_x - std::floor(scaled_x) >= 0.5 ? 1U : 0U;
    const std::size_t y = scaled_y - std::floor(scaled_y) >= 0.5 ? 1U : 0U;
    return y * 2U + x;
}

double intersection_over_union(const normalized_rect& left, const normalized_rect& right) noexcept
{
    const double intersection_left = std::max(left.x, right.x);
    const double intersection_top = std::max(left.y, right.y);
    const double intersection_right = std::min(left.x + left.width, right.x + right.width);
    const double intersection_bottom = std::min(left.y + left.height, right.y + right.height);
    const double intersection_width = std::max(0.0, intersection_right - intersection_left);
    const double intersection_height = std::max(0.0, intersection_bottom - intersection_top);
    const double intersection = intersection_width * intersection_height;
    const double area = left.width * left.height + right.width * right.height - intersection;
    return area > 0.0 ? intersection / area : 0.0;
}

std::string normalize_text(const std::string_view text)
{
    std::string normalized;
    normalized.reserve(text.size());
    bool pending_space = false;
    for (const unsigned char character : text)
    {
        if (std::isspace(character) != 0)
        {
            pending_space = !normalized.empty();
            continue;
        }
        if (pending_space)
        {
            normalized.push_back(' ');
            pending_space = false;
        }
        normalized.push_back(static_cast<char>(character));
    }
    return normalized;
}

bool is_prefix_extension(const std::string& previous, const std::string& current) noexcept
{
    return current.size() >= previous.size() &&
        current.compare(0U, previous.size(), previous) == 0;
}
}

text_box_tracker::text_box_tracker(const text_tracker_config config) : config_(config)
{
    if (config_.stable_frame_count == 0U ||
        config_.minimum_delay < std::chrono::milliseconds::zero() ||
        config_.maximum_wait <= std::chrono::milliseconds::zero() ||
        config_.maximum_wait < config_.minimum_delay ||
        config_.continuation_window <= std::chrono::milliseconds::zero() ||
        config_.continuation_window > std::chrono::seconds(5))
    {
        throw std::invalid_argument("text tracker stabilization settings are invalid");
    }
}

track_observation text_box_tracker::observe(
    const text_box box,
    std::string text,
    const scheduling::steady_clock::time_point observed_at,
    const std::uint64_t generation)
{
    if (!valid(box.bounds) || box.candidate_id == 0U || generation == 0U)
        throw std::invalid_argument("text observation is invalid");
    const std::string normalized = normalize_text(text);
    if (normalized.empty())
    {
        clear(box, observed_at);
        return {};
    }

    auto [associated, uncertain] = associate(box);
    if (associated && tracks_.at(*associated).generation != generation)
    {
        remove_track(*associated);
        associated.reset();
    }
    const std::uint64_t track_id = associated.value_or(create_track(box, generation));
    track_state& state = tracks_.at(track_id);
    if (cell_for(state.bounds) != cell_for(box.bounds) ||
        fine_cell_for(state.bounds) != fine_cell_for(box.bounds))
    {
        remove_from_grid(track_id, state.bounds);
        insert_into_grid(track_id, box.bounds);
    }
    candidate_tracks_[box.candidate_id] = track_id;
    state.candidate_id = box.candidate_id;
    state.bounds = box.bounds;

    if (state.pending_text.empty())
    {
        state.pending_text = normalized;
        state.pending_since = observed_at;
        state.sequence_started = observed_at;
        state.consecutive_frames = 1U;
    }
    else if (state.pending_text == normalized)
    {
        ++state.consecutive_frames;
    }
    else
    {
        state.pending_text = normalized;
        state.pending_since = observed_at;
        state.consecutive_frames = 1U;
        if (state.has_stable_text) state.sequence_started = observed_at;
    }

    if (state.has_stable_text && state.pending_text == state.stable_text)
    {
        return {track_id, true, false, uncertain, true, state.source_event_id};
    }

    const bool normally_stable = state.consecutive_frames >= config_.stable_frame_count &&
        observed_at - state.pending_since >= config_.minimum_delay;
    const bool forced = observed_at - state.sequence_started >= config_.maximum_wait;
    if (!normally_stable && !forced)
        return {track_id, false, false, uncertain, false, std::nullopt};

    bool reused = false;
    if (state.source_event_id && state.has_stable_text &&
        is_prefix_extension(state.stable_text, state.pending_text) &&
        observed_at - state.last_event_at <= config_.continuation_window)
    {
        reused = true;
    }
    else
    {
        state.source_event_id = next_event_id_++;
    }
    state.stable_text = state.pending_text;
    state.has_stable_text = true;
    state.last_event_at = observed_at;
    return {track_id, true, forced && !normally_stable, uncertain, reused, state.source_event_id};
}

void text_box_tracker::clear(
    const text_box box,
    const scheduling::steady_clock::time_point observed_at)
{
    static_cast<void>(observed_at);
    const auto candidate = candidate_tracks_.find(box.candidate_id);
    if (candidate != candidate_tracks_.end()) remove_track(candidate->second);
}

std::pair<std::optional<std::uint64_t>, bool> text_box_tracker::associate(
    const text_box& box) const
{
    const auto direct = candidate_tracks_.find(box.candidate_id);
    if (direct != candidate_tracks_.end())
    {
        const auto track = tracks_.find(direct->second);
        if (track != tracks_.end() && intersection_over_union(track->second.bounds, box.bounds) > 0.0)
            return {track->first, false};
    }

    const auto [cell_x, cell_y] = cell_for(box.bounds);
    std::optional<std::uint64_t> best;
    double best_score = 0.0;
    std::size_t scored = 0U;
    for (int y = std::max(0, cell_y - 1); y <= std::min(grid_dimension - 1, cell_y + 1); ++y)
    {
        for (int x = std::max(0, cell_x - 1); x <= std::min(grid_dimension - 1, cell_x + 1); ++x)
        {
            const auto cell = grid_.find({x, y});
            if (cell == grid_.end()) continue;
            const std::vector<std::uint64_t>& entries = cell->second.subdivided
                ? cell->second.fine[fine_cell_for(box.bounds)]
                : cell->second.coarse;
            if (entries.size() > maximum_cell_scan) return {std::nullopt, true};
            for (const std::uint64_t track_id : entries)
            {
                const auto track = tracks_.find(track_id);
                if (track == tracks_.end()) continue;
                const double score = intersection_over_union(track->second.bounds, box.bounds);
                if (score <= 0.0) continue;
                if (scored >= maximum_scored_candidates) return {std::nullopt, true};
                ++scored;
                if (!best || score > best_score || (score == best_score && track_id < *best))
                {
                    best = track_id;
                    best_score = score;
                }
            }
        }
    }
    return {best, false};
}

std::uint64_t text_box_tracker::create_track(
    const text_box& box,
    const std::uint64_t generation)
{
    const std::uint64_t track_id = next_track_id_++;
    track_state state{};
    state.track_id = track_id;
    state.candidate_id = box.candidate_id;
    state.generation = generation;
    state.bounds = box.bounds;
    tracks_.emplace(track_id, std::move(state));
    candidate_tracks_[box.candidate_id] = track_id;
    insert_into_grid(track_id, box.bounds);
    return track_id;
}

void text_box_tracker::remove_track(const std::uint64_t track_id)
{
    const auto track = tracks_.find(track_id);
    if (track == tracks_.end()) return;
    const auto candidate = candidate_tracks_.find(track->second.candidate_id);
    if (candidate != candidate_tracks_.end() && candidate->second == track_id)
        candidate_tracks_.erase(candidate);
    remove_from_grid(track_id, track->second.bounds);
    tracks_.erase(track);
}

void text_box_tracker::insert_into_grid(
    const std::uint64_t track_id,
    const normalized_rect& bounds)
{
    cell_bucket& bucket = grid_[cell_for(bounds)];
    if (bucket.subdivided)
    {
        bucket.fine[fine_cell_for(bounds)].push_back(track_id);
        return;
    }
    bucket.coarse.push_back(track_id);
    if (bucket.coarse.size() <= maximum_cell_scan) return;
    for (const std::uint64_t existing_id : bucket.coarse)
    {
        const auto existing = tracks_.find(existing_id);
        if (existing != tracks_.end())
            bucket.fine[fine_cell_for(existing->second.bounds)].push_back(existing_id);
    }
    bucket.coarse.clear();
    bucket.subdivided = true;
}

void text_box_tracker::remove_from_grid(
    const std::uint64_t track_id,
    const normalized_rect& bounds)
{
    const auto cell = grid_.find(cell_for(bounds));
    if (cell == grid_.end()) return;
    if (cell->second.subdivided)
        std::erase(cell->second.fine[fine_cell_for(bounds)], track_id);
    else
        std::erase(cell->second.coarse, track_id);
    const bool empty = cell->second.subdivided
        ? std::all_of(cell->second.fine.begin(), cell->second.fine.end(),
            [](const auto& entries) { return entries.empty(); })
        : cell->second.coarse.empty();
    if (empty) grid_.erase(cell);
}
}
