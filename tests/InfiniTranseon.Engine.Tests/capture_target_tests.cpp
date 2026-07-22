#include "infini/capture/capture_targets.hpp"
#include "infini/capture/capture_session_manager.hpp"

#include <cassert>
#include <memory>
#include <vector>

namespace {

struct fake_counts final {
    int created{};
    int started{};
    int stopped{};
    infini::capture::capture_source::frame_callback on_frame;
    infini::capture::capture_source::lifecycle_callback on_lifecycle;
};

class fake_capture_source final : public infini::capture::capture_source {
public:
    fake_capture_source(
        const infini::capture::capture_source_descriptor descriptor,
        std::shared_ptr<fake_counts> counts)
        : descriptor_(descriptor), counts_(std::move(counts)) { ++counts_->created; }

    void start(frame_callback on_frame, lifecycle_callback on_lifecycle) override {
        assert(on_frame);
        on_frame_ = std::move(on_frame);
        on_lifecycle_ = std::move(on_lifecycle);
        counts_->on_frame = on_frame_;
        counts_->on_lifecycle = on_lifecycle_;
        running_ = true;
        ++counts_->started;
    }

    void stop() noexcept override {
        if (!running_) return;
        running_ = false;
        ++counts_->stopped;
    }

    [[nodiscard]] infini::capture::capture_source_descriptor descriptor() const noexcept override {
        return descriptor_;
    }
    [[nodiscard]] bool running() const noexcept override { return running_; }

private:
    infini::capture::capture_source_descriptor descriptor_{};
    std::shared_ptr<fake_counts> counts_;
    frame_callback on_frame_;
    lifecycle_callback on_lifecycle_;
    bool running_{};
};

} // namespace

int main() {
    using namespace infini::capture;

    const std::vector<monitor_geometry> monitors{
        {1U, {-1920, 0, 0, 1080}, 1U},
        {2U, {0, 0, 2560, 1440}, 2U},
    };
    const auto pieces = split_desktop_region({-100, 100, 100, 300}, monitors);
    assert(pieces.size() == 2U);
    assert(pieces[0].monitor_key == 1U);
    assert((pieces[0].monitor_pixels == pixel_rect{-100, 100, 0, 300}));
    assert((pieces[0].source_pixels == pixel_rect{1820, 100, 1920, 300}));
    assert(pieces[1].monitor_key == 2U);
    assert((pieces[1].source_pixels == pixel_rect{0, 100, 100, 300}));
    assert((map_normalized_region_to_source(
        {1820, 100, 1920, 300}, 0.25, 0.25, 0.5, 0.5) ==
        pixel_rect{1845, 150, 1895, 250}));

    capture_source_registry registry;
    const physical_source_identity window_source{physical_source_kind::window, 42U, 9U};
    assert(registry.attach(100U, window_source));
    assert(registry.attach(101U, window_source));
    assert(registry.source_count() == 1U);
    assert(registry.logical_target_count(window_source) == 2U);
    assert(!registry.attach(101U, {physical_source_kind::monitor, 42U, 9U}));
    assert(registry.detach(100U));
    assert(registry.source_count() == 1U);
    assert(registry.detach(101U));
    assert(registry.source_count() == 0U);

    target_tracker tracker({L"game.exe", L"GameWindowClass"});
    tracker.observe({10U, L"game.exe", L"GameWindowClass", true, false, false, true});
    assert(tracker.state().lifecycle == tracked_target_lifecycle::available);
    tracker.observe({10U, L"game.exe", L"GameWindowClass", false, true, false, false});
    assert(tracker.state().lifecycle == tracked_target_lifecycle::minimized);
    tracker.closed(10U);
    assert(tracker.state().lifecycle == tracked_target_lifecycle::waiting_for_match);

    tracker.observe({11U, L"game.exe", L"WrongClass", true, false, false, true});
    assert(tracker.state().lifecycle == tracked_target_lifecycle::waiting_for_match);
    tracker.observe({12U, L"game.exe", L"GameWindowClass", true, false, true, true});
    assert(tracker.state().lifecycle == tracked_target_lifecycle::hidden);
    assert(tracker.state().window_key == 12U);

    auto counts = std::make_shared<fake_counts>();
    capture_session_manager sessions([counts](const capture_source_descriptor descriptor) {
        return std::make_unique<fake_capture_source>(descriptor, counts);
    });
    const capture_source_descriptor descriptor{
        77U,
        capture_source_kind::monitor,
        nullptr,
        reinterpret_cast<HMONITOR>(static_cast<std::uintptr_t>(77U)),
    };
    const physical_source_identity shared_monitor{
        physical_source_kind::monitor, 77U, pack_adapter_luid(0, 9U)};
    int first_lifecycle{};
    int second_lifecycle{};
    int first_frames{};
    int second_frames{};
    assert(sessions.attach(501U, shared_monitor, descriptor, [&first_frames](const auto&) { ++first_frames; },
        [&first_lifecycle](const auto) { ++first_lifecycle; }));
    assert(sessions.attach(502U, shared_monitor, descriptor, [&second_frames](const auto&) { ++second_frames; },
        [&second_lifecycle](const auto) { ++second_lifecycle; }));
    assert(counts->created == 1);
    assert(counts->started == 1);
    assert(sessions.physical_source_count() == 1U);
    assert(sessions.logical_target_count(shared_monitor) == 2U);
    assert(!sessions.attach(501U, shared_monitor, descriptor, [](const auto&) {}, {}));
    auto wrong_adapter_frame = std::make_shared<captured_frame>();
    wrong_adapter_frame->adapter_luid.LowPart = 10U;
    counts->on_frame(wrong_adapter_frame);
    assert(first_frames == 0 && second_frames == 0);
    assert(first_lifecycle == 1 && second_lifecycle == 1);
    auto expected_adapter_frame = std::make_shared<captured_frame>();
    expected_adapter_frame->adapter_luid.LowPart = 9U;
    counts->on_frame(expected_adapter_frame);
    assert(first_frames == 1 && second_frames == 1);
    assert(sessions.detach(501U));
    assert(counts->stopped == 0);
    assert(sessions.detach(502U));
    assert(counts->stopped == 1);
    assert(sessions.physical_source_count() == 0U);
    return 0;
}
