#include "infini/overlay/overlay_runtime.hpp"

#ifdef _WIN32

#include "infini/overlay/overlay_compositor.hpp"

#include <wrl/client.h>

#include <chrono>
#include <condition_variable>
#include <algorithm>
#include <mutex>
#include <stdexcept>
#include <thread>
#include <utility>

namespace infini::overlay {

struct overlay_runtime::implementation final {
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    RECT bounds{};
    target_visibility visibility{true, false, false, true};
    event_callback callback;
    target_state_provider target_provider;
    std::mutex gate;
    std::condition_variable changed;
    std::shared_ptr<const desired_state> latest_state;
    std::uint64_t last_submitted_revision{};
    bool target_changed{};
    bool started{};
    bool initialized{};
    bool initialization_succeeded{};
    bool stopping{};
    bool stopped_notified{};
    std::thread worker;

    void notify(const overlay_runtime_status status, const std::uint64_t revision) noexcept {
        if (!callback) return;
        try { callback({status, revision}); } catch (...) { }
    }

    void run() noexcept {
        const HRESULT apartment_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const bool apartment_owned = SUCCEEDED(apartment_result);
        overlay_window window;
        overlay_compositor compositor;
        overlay_window_error window_result = window.create(bounds);
        compositor_error compositor_result = compositor_error::invalid_argument;
        if (window_result == overlay_window_error::none) {
            compositor_result = compositor.initialize(
                window.handle(),
                device.Get(),
                static_cast<std::uint32_t>(bounds.right - bounds.left),
                static_cast<std::uint32_t>(bounds.bottom - bounds.top));
        }
        {
            std::scoped_lock lock(gate);
            initialization_succeeded = window_result == overlay_window_error::none &&
                compositor_result == compositor_error::none;
            initialized = true;
        }
        changed.notify_all();
        if (!initialization_succeeded) {
            notify(window_result == overlay_window_error::capture_exclusion_failed
                    ? overlay_runtime_status::capture_exclusion_failed
                    : overlay_runtime_status::device_failed,
                0U);
            if (apartment_owned) CoUninitialize();
            return;
        }

        renderer_state revisions;
        auto next_target_poll = std::chrono::steady_clock::now();
        auto next_cursor_poll = std::chrono::steady_clock::now();
        std::vector<identity> rendered_regions;
        const auto cursor_position = [&window]() -> std::optional<point_f> {
            POINT cursor{};
            if (GetCursorPos(&cursor) == FALSE ||
                ScreenToClient(window.handle(), &cursor) == FALSE) return std::nullopt;
            return point_f{
                static_cast<float>(cursor.x),
                static_cast<float>(cursor.y)};
        };
        const auto region_ids = [](const desired_state& state) {
            std::vector<identity> result;
            result.reserve(state.regions.size());
            for (const region& value : state.regions) result.push_back(value.id);
            return result;
        };
        while (window.pump_once()) {
            std::shared_ptr<const desired_state> state;
            RECT new_bounds{};
            target_visibility new_visibility{};
            bool bounds_changed = false;
            {
                std::unique_lock lock(gate);
                changed.wait_for(lock, std::chrono::milliseconds{16}, [this] {
                    return stopping || latest_state || target_changed;
                });
                if (stopping) break;
                state = std::exchange(latest_state, {});
                if (target_changed) {
                    new_bounds = bounds;
                    new_visibility = visibility;
                    target_changed = false;
                    bounds_changed = true;
                }
            }
            const auto now = std::chrono::steady_clock::now();
            if (target_provider && now >= next_target_poll) {
                next_target_poll = now + std::chrono::milliseconds{100};
                try {
                    const auto observed = target_provider();
                    if (!observed.has_value()) {
                        static_cast<void>(window.set_visible(false));
                    } else {
                        new_bounds = observed->bounds;
                        new_visibility = observed->visibility;
                        bounds_changed = true;
                    }
                } catch (...) {
                    static_cast<void>(window.set_visible(false));
                }
            }
            if (bounds_changed) {
                if (window.set_bounds(new_bounds) != overlay_window_error::none ||
                    compositor.resize(
                        static_cast<std::uint32_t>(new_bounds.right - new_bounds.left),
                        static_cast<std::uint32_t>(new_bounds.bottom - new_bounds.top)) != compositor_error::none) {
                    notify(overlay_runtime_status::device_failed, revisions.applied_revision());
                    break;
                }
                static_cast<void>(window.set_visible(should_show_overlay(new_visibility)));
                rendered_regions.clear();
            }
            if (!state) {
                if (now < next_cursor_poll || revisions.current() == nullptr ||
                    !std::ranges::any_of(revisions.current()->regions, [](const region& value) {
                        return value.style.background == background_mode::floating_panel;
                    })) continue;
                next_cursor_poll = now + std::chrono::milliseconds{100};
                desired_state visible = visible_state_for_cursor(
                    *revisions.current(), cursor_position());
                std::vector<identity> visible_ids = region_ids(visible);
                if (visible_ids == rendered_regions) continue;
                const float dpi_scale = static_cast<float>(
                    GetDpiForWindow(window.handle())) / 96.0F;
                if (compositor.render(visible, dpi_scale) != compositor_error::none) {
                    notify(overlay_runtime_status::device_failed,
                        revisions.applied_revision());
                    break;
                }
                rendered_regions = std::move(visible_ids);
                continue;
            }
            std::optional<desired_state> previous_state;
            std::optional<refinement_transition> transition;
            if (revisions.current() != nullptr) {
                previous_state = *revisions.current();
                transition = plan_refinement_transition(*previous_state, *state);
            }
            const apply_error apply_result = revisions.apply(*state, window.capture_exclusion_active());
            if (apply_result == apply_error::stale_revision) {
                notify(overlay_runtime_status::superseded, state->revision);
                continue;
            }
            if (apply_result != apply_error::none) {
                notify(apply_result == apply_error::capture_exclusion_required
                        ? overlay_runtime_status::capture_exclusion_failed
                        : apply_result == apply_error::background_unavailable
                            ? overlay_runtime_status::background_unavailable
                            : overlay_runtime_status::invalid_state,
                    state->revision);
                continue;
            }
            const float dpi_scale = static_cast<float>(GetDpiForWindow(window.handle())) / 96.0F;
            desired_state visible = visible_state_for_cursor(*state, cursor_position());
            bool transition_superseded = false;
            bool transition_stopped = false;
            if (transition.has_value() && transition->crossfade_milliseconds > 0U &&
                previous_state.has_value()) {
                desired_state previous_visible = visible_state_for_cursor(
                    *previous_state, cursor_position());
                const auto started_at = std::chrono::steady_clock::now();
                const auto duration = std::chrono::milliseconds{
                    transition->crossfade_milliseconds};
                while (true) {
                    const auto elapsed = std::chrono::steady_clock::now() - started_at;
                    const float progress = (std::min)(
                        1.0F,
                        std::chrono::duration<float>(elapsed).count() /
                            std::chrono::duration<float>(duration).count());
                    if (compositor.render(
                            visible, dpi_scale, &previous_visible, progress,
                            transition->crossfade_milliseconds) !=
                        compositor_error::none) {
                        notify(overlay_runtime_status::device_failed, state->revision);
                        transition_stopped = true;
                        break;
                    }
                    if (progress >= 1.0F) break;
                    if (!window.pump_once()) {
                        transition_stopped = true;
                        break;
                    }
                    std::unique_lock lock(gate);
                    changed.wait_for(lock, std::chrono::milliseconds{8}, [this] {
                        return stopping || latest_state != nullptr;
                    });
                    if (stopping) {
                        transition_stopped = true;
                        break;
                    }
                    if (latest_state) {
                        transition_superseded = true;
                        break;
                    }
                }
            } else if (compositor.render(visible, dpi_scale) != compositor_error::none) {
                notify(overlay_runtime_status::device_failed, state->revision);
                break;
            }
            if (transition_stopped) break;
            if (transition_superseded) {
                notify(overlay_runtime_status::superseded, state->revision);
                continue;
            }
            rendered_regions = region_ids(visible);
            next_cursor_poll = now + std::chrono::milliseconds{100};
            notify(overlay_runtime_status::applied, state->revision);
        }
        static_cast<void>(window.set_visible(false));
        if (apartment_owned) CoUninitialize();
    }
};

overlay_runtime::overlay_runtime(
    ID3D11Device* const device,
    const RECT initial_bounds,
    event_callback callback,
    target_state_provider target_provider)
    : impl_(std::make_shared<implementation>()) {
    if (device == nullptr || initial_bounds.right <= initial_bounds.left ||
        initial_bounds.bottom <= initial_bounds.top || !callback)
        throw std::invalid_argument("invalid overlay runtime configuration");
    impl_->device = device;
    impl_->bounds = initial_bounds;
    impl_->callback = std::move(callback);
    impl_->target_provider = std::move(target_provider);
}

overlay_runtime::~overlay_runtime() { stop(); }

bool overlay_runtime::start() {
    std::unique_lock lock(impl_->gate);
    if (impl_->started) return impl_->initialization_succeeded;
    impl_->started = true;
    impl_->worker = std::thread([state = impl_] { state->run(); });
    impl_->changed.wait(lock, [state = impl_] { return state->initialized; });
    return impl_->initialization_succeeded;
}

bool overlay_runtime::submit(std::shared_ptr<const desired_state> state) {
    if (!state || state->revision == 0U) return false;
    std::shared_ptr<const desired_state> replaced;
    {
        std::scoped_lock lock(impl_->gate);
        if (!impl_->started || impl_->stopping || state->revision <= impl_->last_submitted_revision) return false;
        impl_->last_submitted_revision = state->revision;
        replaced = std::exchange(impl_->latest_state, std::move(state));
    }
    if (replaced) impl_->notify(overlay_runtime_status::superseded, replaced->revision);
    impl_->changed.notify_one();
    return true;
}

bool overlay_runtime::update_target(const RECT bounds, const target_visibility visibility) {
    if (bounds.right <= bounds.left || bounds.bottom <= bounds.top) return false;
    {
        std::scoped_lock lock(impl_->gate);
        if (!impl_->started || impl_->stopping) return false;
        impl_->bounds = bounds;
        impl_->visibility = visibility;
        impl_->target_changed = true;
    }
    impl_->changed.notify_one();
    return true;
}

void overlay_runtime::stop() noexcept {
    bool should_notify = false;
    {
        std::scoped_lock lock(impl_->gate);
        if (!impl_->started) return;
        if (!impl_->stopping) impl_->stopping = true;
        if (!impl_->stopped_notified) {
            impl_->stopped_notified = true;
            should_notify = true;
        }
    }
    impl_->changed.notify_one();
    if (impl_->worker.joinable()) {
        if (impl_->worker.get_id() == std::this_thread::get_id()) impl_->worker.detach();
        else impl_->worker.join();
    }
    if (should_notify)
        impl_->notify(overlay_runtime_status::stopped, impl_->last_submitted_revision);
}

} // namespace infini::overlay

#endif
