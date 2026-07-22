#pragma once

#include <cstdint>
#include <map>
#include <mutex>

#include <infini/scheduling/work_types.hpp>

namespace infini::scheduling
{
class generation_registry final
{
public:
    generation_token activate(work_key key, std::uint64_t profile_revision);
    generation_token rollover(work_key key, std::uint64_t profile_revision);
    void cancel(work_key key);
    [[nodiscard]] bool is_current(const generation_token& token) const;

private:
    struct generation_state
    {
        std::uint64_t generation{};
        std::uint64_t profile_revision{};
        bool active{};
    };

    mutable std::mutex mutex_{};
    std::map<work_key, generation_state> states_{};
};
}
