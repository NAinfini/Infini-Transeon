#include "infini/capture/capture_session_manager.hpp"

#ifdef _WIN32

#include <stdexcept>
#include <unordered_map>
#include <utility>
#include <vector>

namespace infini::capture {

struct capture_session_manager::shared_state final {
    struct subscription final {
        capture_source::frame_callback on_frame;
        capture_source::lifecycle_callback on_lifecycle;
    };

    struct source_entry final {
        physical_source_identity identity{};
        capture_source_descriptor descriptor{};
        std::unique_ptr<capture_source> source;
        std::unordered_map<std::uint64_t, subscription> subscriptions;
    };

    mutable std::mutex gate;
    std::unordered_map<physical_source_identity, std::shared_ptr<source_entry>,
        physical_source_identity_hash> sources;
    std::unordered_map<std::uint64_t, physical_source_identity> logical_sources;
};

namespace {

[[nodiscard]] bool descriptor_matches(
    const physical_source_identity identity,
    const capture_source_descriptor descriptor) noexcept {
    const auto expected_kind = identity.kind == physical_source_kind::window
        ? capture_source_kind::window
        : capture_source_kind::monitor;
    return identity.native_key != 0U && identity.adapter_key != 0U &&
        descriptor.key == identity.native_key && descriptor.kind == expected_kind &&
        ((descriptor.kind == capture_source_kind::window && descriptor.window != nullptr) ||
            (descriptor.kind == capture_source_kind::monitor && descriptor.monitor != nullptr));
}

} // namespace

capture_session_manager::capture_session_manager(source_factory factory)
    : factory_(std::move(factory)), state_(std::make_shared<shared_state>()) {
    if (!factory_) throw std::invalid_argument("capture source factory is required");
}

capture_session_manager::~capture_session_manager() { stop_all(); }

bool capture_session_manager::attach(
    const std::uint64_t logical_target_id,
    const physical_source_identity identity,
    const capture_source_descriptor descriptor,
    capture_source::frame_callback on_frame,
    capture_source::lifecycle_callback on_lifecycle) {
    if (logical_target_id == 0U || !descriptor_matches(identity, descriptor) || !on_frame)
        throw std::invalid_argument("invalid logical capture subscription");

    std::scoped_lock control_lock(control_gate_);
    std::shared_ptr<shared_state::source_entry> entry;
    {
        std::scoped_lock state_lock(state_->gate);
        if (state_->logical_sources.contains(logical_target_id)) return false;
        const auto existing = state_->sources.find(identity);
        if (existing != state_->sources.end()) {
            if (!(existing->second->descriptor.key == descriptor.key &&
                existing->second->descriptor.kind == descriptor.kind &&
                existing->second->descriptor.window == descriptor.window &&
                existing->second->descriptor.monitor == descriptor.monitor)) {
                throw std::invalid_argument("physical capture descriptor changed without a lifecycle transition");
            }
            existing->second->subscriptions.emplace(
                logical_target_id,
                shared_state::subscription{std::move(on_frame), std::move(on_lifecycle)});
            state_->logical_sources.emplace(logical_target_id, identity);
            return true;
        }
    }

    auto source = factory_(descriptor);
    if (!source) throw std::runtime_error("capture source factory returned no source");
    entry = std::make_shared<shared_state::source_entry>();
    entry->identity = identity;
    entry->descriptor = descriptor;
    entry->source = std::move(source);
    entry->subscriptions.emplace(
        logical_target_id,
        shared_state::subscription{std::move(on_frame), std::move(on_lifecycle)});
    {
        std::scoped_lock state_lock(state_->gate);
        state_->sources.emplace(identity, entry);
        state_->logical_sources.emplace(logical_target_id, identity);
    }

    const std::weak_ptr<shared_state> weak_state = state_;
    try {
        entry->source->start(
            [weak_state, identity](std::shared_ptr<captured_frame> frame) {
                const auto state = weak_state.lock();
                if (!state) return;
                std::vector<capture_source::frame_callback> callbacks;
                std::vector<capture_source::lifecycle_callback> lifecycle_callbacks;
                {
                    std::scoped_lock lock(state->gate);
                    const auto found = state->sources.find(identity);
                    if (found == state->sources.end()) return;
                    const bool adapter_matches = frame &&
                        pack_adapter_luid(frame->adapter_luid.HighPart, frame->adapter_luid.LowPart) ==
                            identity.adapter_key;
                    if (adapter_matches) {
                        callbacks.reserve(found->second->subscriptions.size());
                        for (const auto& [_, subscription] : found->second->subscriptions)
                            callbacks.push_back(subscription.on_frame);
                    } else {
                        lifecycle_callbacks.reserve(found->second->subscriptions.size());
                        for (const auto& [_, subscription] : found->second->subscriptions)
                            if (subscription.on_lifecycle)
                                lifecycle_callbacks.push_back(subscription.on_lifecycle);
                    }
                }
                if (!lifecycle_callbacks.empty()) {
                    for (const auto& callback : lifecycle_callbacks)
                        callback(capture_lifecycle::adapter_changed);
                    return;
                }
                for (const auto& callback : callbacks) callback(frame);
            },
            [weak_state, identity](const capture_lifecycle lifecycle) {
                const auto state = weak_state.lock();
                if (!state) return;
                std::vector<capture_source::lifecycle_callback> callbacks;
                {
                    std::scoped_lock lock(state->gate);
                    const auto found = state->sources.find(identity);
                    if (found == state->sources.end()) return;
                    callbacks.reserve(found->second->subscriptions.size());
                    for (const auto& [_, subscription] : found->second->subscriptions)
                        if (subscription.on_lifecycle) callbacks.push_back(subscription.on_lifecycle);
                }
                for (const auto& callback : callbacks) callback(lifecycle);
            });
    } catch (...) {
        std::scoped_lock state_lock(state_->gate);
        state_->logical_sources.erase(logical_target_id);
        state_->sources.erase(identity);
        throw;
    }
    return true;
}

bool capture_session_manager::detach(const std::uint64_t logical_target_id) noexcept {
    std::scoped_lock control_lock(control_gate_);
    std::shared_ptr<shared_state::source_entry> removed;
    {
        std::scoped_lock state_lock(state_->gate);
        const auto target = state_->logical_sources.find(logical_target_id);
        if (target == state_->logical_sources.end()) return false;
        const auto source = state_->sources.find(target->second);
        if (source != state_->sources.end()) {
            source->second->subscriptions.erase(logical_target_id);
            if (source->second->subscriptions.empty()) {
                removed = source->second;
                state_->sources.erase(source);
            }
        }
        state_->logical_sources.erase(target);
    }
    if (removed) removed->source->stop();
    return true;
}

void capture_session_manager::stop_all() noexcept {
    std::scoped_lock control_lock(control_gate_);
    std::vector<std::shared_ptr<shared_state::source_entry>> removed;
    {
        std::scoped_lock state_lock(state_->gate);
        removed.reserve(state_->sources.size());
        for (auto& [_, entry] : state_->sources) removed.push_back(std::move(entry));
        state_->sources.clear();
        state_->logical_sources.clear();
    }
    for (const auto& entry : removed) entry->source->stop();
}

std::size_t capture_session_manager::physical_source_count() const noexcept {
    std::scoped_lock lock(state_->gate);
    return state_->sources.size();
}

std::size_t capture_session_manager::logical_target_count(
    const physical_source_identity identity) const noexcept {
    std::scoped_lock lock(state_->gate);
    const auto found = state_->sources.find(identity);
    return found == state_->sources.end() ? 0U : found->second->subscriptions.size();
}

} // namespace infini::capture

#endif
