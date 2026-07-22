#include <infini/scheduling/generation_registry.hpp>

namespace infini::scheduling
{
generation_token generation_registry::activate(
    const work_key key,
    const std::uint64_t profile_revision)
{
    std::scoped_lock lock(mutex_);
    generation_state& state = states_[key];
    if (state.generation == 0U) state.generation = 1U;
    state.profile_revision = profile_revision;
    state.active = true;
    return {key, state.generation, state.profile_revision};
}

generation_token generation_registry::rollover(
    const work_key key,
    const std::uint64_t profile_revision)
{
    std::scoped_lock lock(mutex_);
    generation_state& state = states_[key];
    ++state.generation;
    if (state.generation == 0U) ++state.generation;
    state.profile_revision = profile_revision;
    state.active = true;
    return {key, state.generation, state.profile_revision};
}

void generation_registry::cancel(const work_key key)
{
    std::scoped_lock lock(mutex_);
    const auto found = states_.find(key);
    if (found != states_.end()) found->second.active = false;
}

bool generation_registry::is_current(const generation_token& token) const
{
    std::scoped_lock lock(mutex_);
    const auto found = states_.find(token.key);
    return found != states_.end() && found->second.active &&
        found->second.generation == token.generation &&
        found->second.profile_revision == token.profile_revision;
}
}
