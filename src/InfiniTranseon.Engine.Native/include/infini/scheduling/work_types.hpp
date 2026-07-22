#pragma once

#include <chrono>
#include <compare>
#include <cstdint>
#include <string>

namespace infini::scheduling
{
using steady_clock = std::chrono::steady_clock;

struct work_key
{
    std::uint64_t target_id{};
    std::uint64_t area_id{};
    auto operator<=>(const work_key&) const = default;
};

struct generation_token
{
    work_key key{};
    std::uint64_t generation{};
    std::uint64_t profile_revision{};
    auto operator<=>(const generation_token&) const = default;
};

enum class work_priority : std::uint8_t
{
    p0,
    p1,
    p2,
    p3,
};

enum class area_mode : std::uint8_t
{
    user_region,
    full_target,
    remaining_area,
};

struct detection_work_item
{
    work_key key{};
    std::uint64_t capture_frame_reference{};
    std::uint64_t detection_epoch{};
    std::uint64_t profile_revision{};
    steady_clock::time_point deadline{};
};

struct recognition_work_item
{
    work_key key{};
    generation_token token{};
    std::uint64_t crop_lease_id{};
    std::uint64_t frame_sequence{};
    std::uint64_t device_epoch{};
    work_priority priority{work_priority::p3};
    steady_clock::time_point enqueued_at{};
    steady_clock::time_point deadline{};
    std::chrono::milliseconds configured_interval{};
    std::string reason{};
};
}
