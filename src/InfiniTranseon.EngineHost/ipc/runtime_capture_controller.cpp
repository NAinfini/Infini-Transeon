#include "runtime_capture_controller.h"

#include <infini/capture/capture_session_manager.hpp>
#include <infini/capture/capture_targets.hpp>
#include <infini/capture/windows_capture_source.hpp>
#include <infini/imaging/device_runtime.hpp>
#include <infini/imaging/change_detector.hpp>
#include <infini/overlay/background_treatment.hpp>
#include <infini/overlay/overlay_runtime.hpp>
#include <infini/ocr/cloud_crop_encoder.hpp>
#include <infini/ocr/preprocessing_pipeline.hpp>
#include <infini/ocr/texture_crop_readback.hpp>
#include <infini/ocr/windows_media_ocr.hpp>
#include <infini/scheduling/latest_worker.hpp>

#include <d3d11.h>
#include <dwmapi.h>
#include <dxgi1_4.h>
#include <shobjidl.h>
#include <windows.h>
#include <wrl/client.h>
#include <winrt/base.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <compare>
#include <cstddef>
#include <functional>
#include <limits>
#include <map>
#include <memory>
#include <mutex>
#include <numeric>
#include <set>
#include <span>
#include <stdexcept>
#include <unordered_map>
#include <unordered_set>
#include <utility>
#include <vector>

namespace infini::runtime
{
std::uint64_t calculate_gpu_pool_limit(
    const std::uint64_t operating_system_budget) noexcept
{
    constexpr std::uint64_t maximum_gpu_bytes_per_adapter =
        1024ULL * 1024U * 1024U;
    constexpr std::uint64_t gpu_budget_divisor = 4U;
    return (std::min)(
        maximum_gpu_bytes_per_adapter,
        operating_system_budget / gpu_budget_divisor);
}

bool runtime_capacity_available(
    const std::size_t current,
    const std::size_t requested,
    const std::size_t limit) noexcept
{
    return current <= limit && requested <= limit - current;
}

bool manual_ocr_allows_region(
    const bool manual_requested,
    const bool policy_suppressed,
    const bool cadence_due,
    const bool request_outstanding) noexcept
{
    return !request_outstanding &&
        (manual_requested || (!policy_suppressed && cadence_due));
}

bool manual_ocr_target_available(
    const std::uint32_t lifecycle_state,
    const bool latest_frame_available) noexcept
{
    if (!latest_frame_available) return false;
    return lifecycle_state == 1U ||
        lifecycle_state == 4U ||
        lifecycle_state == 5U ||
        lifecycle_state == 6U ||
        lifecycle_state == 10U;
}

bool manual_ocr_allows_signature(
    const bool manual_requested,
    const bool meaningfully_changed) noexcept
{
    return manual_requested || meaningfully_changed;
}

namespace
{
using Microsoft::WRL::ComPtr;
using target_key = std::array<std::byte, 16U>;

constexpr std::uint32_t state_available = 0U;
constexpr std::uint32_t state_running = 1U;
constexpr std::uint32_t state_minimized = 2U;
constexpr std::uint32_t state_unsupported = 3U;
constexpr std::uint32_t state_resized = 4U;
constexpr std::uint32_t state_dpi_changed = 5U;
constexpr std::uint32_t state_adapter_changed = 6U;
constexpr std::uint32_t state_device_lost = 7U;
constexpr std::uint32_t state_closed = 8U;
constexpr std::uint32_t state_waiting_for_match = 9U;
constexpr std::uint32_t state_running_with_capture_border = 10U;
constexpr std::uint64_t filetime_to_datetime_ticks = 504'911'232'000'000'000ULL;
constexpr std::uint64_t cloud_request_lifetime_ticks = 150'000'000ULL;

[[nodiscard]] bool preprocess_crop(
    ocr::bgra_image& image,
    const ocr::preprocessing_options& options) noexcept
{
    ocr::rgba_image rgba{};
    ocr::grayscale_image processed{};
    try
    {
        constexpr std::uint64_t maximum_preprocessed_pixels = 4'194'304U;
        const std::uint64_t scaled_width = static_cast<std::uint64_t>(image.width) *
            options.scale;
        const std::uint64_t scaled_height = static_cast<std::uint64_t>(image.height) *
            options.scale;
        if (scaled_height == 0U ||
            scaled_width > maximum_preprocessed_pixels / scaled_height)
            return false;
        rgba.width = static_cast<std::int32_t>(image.width);
        rgba.height = static_cast<std::int32_t>(image.height);
        const std::size_t pixel_count = static_cast<std::size_t>(image.width) * image.height;
        rgba.pixels.resize(pixel_count * 4U);
        for (std::size_t index = 0U; index < pixel_count; ++index)
        {
            rgba.pixels[index * 4U] = std::to_integer<std::uint8_t>(
                image.pixels[index * 4U + 2U]);
            rgba.pixels[index * 4U + 1U] = std::to_integer<std::uint8_t>(
                image.pixels[index * 4U + 1U]);
            rgba.pixels[index * 4U + 2U] = std::to_integer<std::uint8_t>(
                image.pixels[index * 4U]);
            rgba.pixels[index * 4U + 3U] = std::to_integer<std::uint8_t>(
                image.pixels[index * 4U + 3U]);
        }
        processed = ocr::preprocess(rgba, options);
        const std::uint64_t processed_pixels = static_cast<std::uint64_t>(processed.width) *
            static_cast<std::uint64_t>(processed.height);
        if (processed.width <= 0 || processed.height <= 0 ||
            processed_pixels > maximum_preprocessed_pixels)
            throw std::length_error("Preprocessed OCR crop exceeds the pixel ceiling");
        ocr::bgra_image replacement{};
        replacement.width = static_cast<std::uint32_t>(processed.width);
        replacement.height = static_cast<std::uint32_t>(processed.height);
        replacement.stride = replacement.width * 4U;
        replacement.pixels.resize(static_cast<std::size_t>(processed_pixels) * 4U);
        for (std::size_t index = 0U; index < processed_pixels; ++index)
        {
            const std::byte value = static_cast<std::byte>(processed.pixels[index]);
            replacement.pixels[index * 4U] = value;
            replacement.pixels[index * 4U + 1U] = value;
            replacement.pixels[index * 4U + 2U] = value;
            replacement.pixels[index * 4U + 3U] = std::byte{0xff};
        }
        SecureZeroMemory(rgba.pixels.data(), rgba.pixels.size());
        SecureZeroMemory(processed.pixels.data(), processed.pixels.size());
        SecureZeroMemory(image.pixels.data(), image.pixels.size());
        image = std::move(replacement);
        return true;
    }
    catch (...)
    {
        SecureZeroMemory(rgba.pixels.data(), rgba.pixels.size());
        SecureZeroMemory(processed.pixels.data(), processed.pixels.size());
        return false;
    }
}

struct TargetKeyHash final
{
    [[nodiscard]] std::size_t operator()(const target_key& value) const noexcept
    {
        std::size_t result{};
        for (const std::byte item : value)
            result = (result * 131U) ^ std::to_integer<std::uint8_t>(item);
        return result;
    }
};

[[nodiscard]] bool nonzero_identity(const target_key& value) noexcept
{
    return std::ranges::any_of(value,
        [](const std::byte item) { return item != std::byte{}; });
}

[[nodiscard]] std::optional<target_key> create_identity() noexcept
{
    GUID value{};
    if (FAILED(CoCreateGuid(&value))) return std::nullopt;
    target_key result{};
    std::ranges::copy(std::as_bytes(std::span{&value, 1U}), result.begin());
    return result;
}

[[nodiscard]] std::uint64_t utc_datetime_ticks() noexcept
{
    FILETIME file_time{};
    GetSystemTimePreciseAsFileTime(&file_time);
    const ULARGE_INTEGER value{file_time.dwLowDateTime, file_time.dwHighDateTime};
    return value.QuadPart + filetime_to_datetime_ticks;
}

[[nodiscard]] std::uint32_t lifecycle_state(
    const capture::capture_lifecycle lifecycle) noexcept
{
    switch (lifecycle)
    {
    case capture::capture_lifecycle::available: return state_available;
    case capture::capture_lifecycle::running: return state_running;
    case capture::capture_lifecycle::minimized: return state_minimized;
    case capture::capture_lifecycle::resized: return state_resized;
    case capture::capture_lifecycle::dpi_changed: return state_dpi_changed;
    case capture::capture_lifecycle::closed: return state_closed;
    case capture::capture_lifecycle::unsupported: return state_unsupported;
    case capture::capture_lifecycle::device_lost: return state_device_lost;
    case capture::capture_lifecycle::adapter_changed: return state_adapter_changed;
    case capture::capture_lifecycle::border_required:
        return state_running_with_capture_border;
    }
    return state_unsupported;
}

struct AdapterAndMonitor final
{
    ComPtr<IDXGIAdapter1> adapter;
    std::uint64_t adapter_key{};
    HMONITOR monitor{};
    RECT desktop_pixels{};
};

[[nodiscard]] std::vector<AdapterAndMonitor> enumerate_outputs()
{
    ComPtr<IDXGIFactory1> factory;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))))
        throw std::runtime_error("DXGI factory creation failed");

    std::vector<AdapterAndMonitor> outputs;
    for (UINT adapter_index{};; ++adapter_index)
    {
        ComPtr<IDXGIAdapter1> adapter;
        const HRESULT adapter_result = factory->EnumAdapters1(adapter_index, &adapter);
        if (adapter_result == DXGI_ERROR_NOT_FOUND) break;
        if (FAILED(adapter_result)) throw std::runtime_error("DXGI adapter enumeration failed");
        DXGI_ADAPTER_DESC1 adapter_description{};
        if (FAILED(adapter->GetDesc1(&adapter_description)))
            throw std::runtime_error("DXGI adapter description failed");
        const std::uint64_t adapter_key = capture::pack_adapter_luid(
            adapter_description.AdapterLuid.HighPart,
            adapter_description.AdapterLuid.LowPart);
        for (UINT output_index{};; ++output_index)
        {
            ComPtr<IDXGIOutput> output;
            const HRESULT output_result = adapter->EnumOutputs(output_index, &output);
            if (output_result == DXGI_ERROR_NOT_FOUND) break;
            if (FAILED(output_result)) throw std::runtime_error("DXGI output enumeration failed");
            DXGI_OUTPUT_DESC output_description{};
            if (FAILED(output->GetDesc(&output_description)))
                throw std::runtime_error("DXGI output description failed");
            outputs.push_back({
                adapter,
                adapter_key,
                output_description.Monitor,
                output_description.DesktopCoordinates,
            });
        }
    }
    return outputs;
}

[[nodiscard]] std::optional<overlay::overlay_target_state> observe_window_target(
    const HWND window) noexcept
{
    if (window == nullptr || IsWindow(window) == FALSE) return std::nullopt;
    RECT client{};
    POINT origin{};
    if (GetClientRect(window, &client) == FALSE ||
        ClientToScreen(window, &origin) == FALSE ||
        client.right <= client.left || client.bottom <= client.top)
        return std::nullopt;
    DWORD cloaked{};
    const bool is_cloaked = SUCCEEDED(DwmGetWindowAttribute(
        window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked != 0U;
    bool on_current_desktop = !is_cloaked;
    thread_local ComPtr<IVirtualDesktopManager> desktop_manager;
    if (!desktop_manager)
        static_cast<void>(CoCreateInstance(
            CLSID_VirtualDesktopManager,
            nullptr,
            CLSCTX_INPROC_SERVER,
            IID_PPV_ARGS(&desktop_manager)));
    if (desktop_manager)
    {
        BOOL current{};
        if (SUCCEEDED(desktop_manager->IsWindowOnCurrentVirtualDesktop(window, &current)))
            on_current_desktop = current != FALSE;
    }
    const HWND foreground = GetForegroundWindow();
    const bool foreground_match = foreground != nullptr &&
        GetAncestor(foreground, GA_ROOT) == GetAncestor(window, GA_ROOT);
    return overlay::overlay_target_state{
        {origin.x, origin.y, origin.x + client.right, origin.y + client.bottom},
        {
            IsWindowVisible(window) != FALSE && foreground_match,
            IsIconic(window) != FALSE,
            is_cloaked,
            on_current_desktop,
        },
    };
}
}

struct RuntimeCaptureController::Implementation final
{
    struct AdapterContext final
    {
        ComPtr<ID3D11Device> device;
        std::unique_ptr<imaging::device_runtime> runtime;
        std::unique_ptr<capture::capture_session_manager> sessions;
    };

    struct Subscription final
    {
        std::uint64_t adapter_key{};
        std::uint64_t logical_id{};
    };

    struct Status final
    {
        target_key target_id{};
        target_key target_instance_id{};
        std::atomic<std::uint64_t> sequence{};
        std::atomic<std::uint32_t> state{state_available};
        std::atomic<std::uint32_t> pixel_width{1U};
        std::atomic<std::uint32_t> pixel_height{1U};
        std::atomic<std::uint32_t> dpi{96U};
        std::atomic<bool> active{};
    };

    struct OverlayStatus;

    struct OcrTrackKey final
    {
        std::uint8_t area_kind{};
        target_key region_id{};
        target_key text_track_id{};
        auto operator<=>(const OcrTrackKey&) const = default;
    };

    struct AcceptedOcrResult final
    {
        std::uint64_t source_generation{};
        std::uint64_t result_sequence{};
    };

    struct ProcessingState final
    {
        static constexpr std::size_t background_cache_bytes = 67'108'864U;
        static constexpr std::size_t background_frame_bytes = 33'554'432U;

        struct AdapterBinding final
        {
            ID3D11Device* device{};
            imaging::device_runtime* runtime{};
        };

        struct OcrRegionState final
        {
            target_key text_track_id{};
            target_key ocr_run_id{};
            std::uint64_t generation{};
            std::chrono::steady_clock::time_point outstanding_until{};
            std::optional<imaging::change_signature> last_dispatched_signature;
        };

        struct ScheduledCloudRegion final
        {
            ProcessingRegion region;
            OcrExecutionIdentity token;
            AdapterBinding binding;
            bool forced{};
        };

        mutable std::mutex gate;
        std::shared_ptr<const ProcessingConfigurationCommand> configuration;
        std::map<target_key, std::chrono::steady_clock::time_point> next_due;
        std::map<target_key, OcrRegionState> ocr_regions;
        std::map<target_key, std::uint8_t> policy_actions;
        std::map<std::uint64_t, AdapterBinding> bindings;
        std::uint64_t last_frame_sequence{};
        std::uint64_t last_device_epoch{};
        std::uint64_t scheduled_regions{};
        std::uint64_t frames_without_configuration{};
        std::uint64_t cloud_requests_sent{};
        std::uint64_t local_results_sent{};
        std::uint64_t unchanged_regions_skipped{};
        std::uint64_t processing_failures{};
        target_key runtime_epoch{};
        cloud_ocr_callback cloud_ocr;
        ocr_result_callback ocr_result;
        bool processing_supported{};
        std::optional<capture::pixel_rect> target_crop;
        std::shared_ptr<capture::captured_frame> latest_frame;
        std::shared_ptr<capture::captured_frame> reserved_manual_frame;
        bool manual_reserved{};
        bool manual_requested{};
        overlay::background_frame_cache background_cache{background_cache_bytes};
        overlay::background_blur_cache background_blurs;
        std::optional<target_key> non_user_background_key;
        scheduling::latest_worker<std::shared_ptr<capture::captured_frame>> worker;

        ProcessingState(
            const target_key epoch,
            cloud_ocr_callback cloud_callback,
            ocr_result_callback result_callback,
            const bool supported)
            : runtime_epoch(epoch),
              cloud_ocr(std::move(cloud_callback)),
              ocr_result(std::move(result_callback)),
              processing_supported(supported),
              worker([this](std::shared_ptr<capture::captured_frame> frame)
            {
                process(std::move(frame));
            })
        {
            if (!worker.start())
                throw std::runtime_error("Frame processing worker failed to start");
        }

        ~ProcessingState() { worker.stop(); }

        void apply(ProcessingConfigurationCommand command)
        {
            auto next = std::make_shared<const ProcessingConfigurationCommand>(
                std::move(command));
            std::scoped_lock lock(gate);
            configuration = std::move(next);
            next_due.clear();
            ocr_regions.clear();
            manual_reserved = false;
            manual_requested = false;
            reserved_manual_frame.reset();
            background_cache.clear();
            background_blurs.clear();
            non_user_background_key.reset();
        }

        void apply_policy(std::map<target_key, std::uint8_t> actions)
        {
            std::scoped_lock lock(gate);
            policy_actions = std::move(actions);
            next_due.clear();
        }

        void configure_desktop_crop(
            const std::optional<capture::pixel_rect> crop)
        {
            std::scoped_lock lock(gate);
            target_crop = crop;
            processing_supported = crop.has_value();
        }

        void bind(
            const std::uint64_t adapter_key,
            ID3D11Device* const device,
            imaging::device_runtime* const runtime)
        {
            if (adapter_key == 0U || device == nullptr || runtime == nullptr)
                throw std::invalid_argument("Frame processing adapter binding is invalid");
            std::scoped_lock lock(gate);
            bindings.insert_or_assign(adapter_key, AdapterBinding{device, runtime});
        }

        void submit(std::shared_ptr<capture::captured_frame> frame) noexcept
        {
            if (!frame) return;
            {
                std::scoped_lock lock(gate);
                latest_frame = frame;
            }
            static_cast<void>(worker.submit(std::move(frame)));
        }

        [[nodiscard]] std::size_t manual_region_count() const noexcept
        {
            std::scoped_lock lock(gate);
            if (!configuration || !processing_supported) return 0U;
            return static_cast<std::size_t>(std::ranges::count_if(
                configuration->regions,
                [scan_remaining = configuration->scan_remaining_area](
                    const ProcessingRegion& region)
                {
                    return region.area_mode != 2U || scan_remaining;
                }));
        }

        [[nodiscard]] bool manual_frame_available() const noexcept
        {
            std::scoped_lock lock(gate);
            return latest_frame != nullptr;
        }

        [[nodiscard]] bool manual_request_busy() const noexcept
        {
            const auto now = std::chrono::steady_clock::now();
            std::scoped_lock lock(gate);
            return manual_reserved || manual_requested ||
                std::ranges::any_of(
                    ocr_regions,
                    [now](const auto& entry)
                    {
                        return entry.second.outstanding_until > now;
                    });
        }

        [[nodiscard]] bool reserve_manual() noexcept
        {
            const auto now = std::chrono::steady_clock::now();
            std::scoped_lock lock(gate);
            if (manual_reserved || manual_requested || !latest_frame || !configuration ||
                !processing_supported ||
                std::ranges::any_of(
                    ocr_regions,
                    [now](const auto& entry)
                    {
                        return entry.second.outstanding_until > now;
                    }) ||
                std::ranges::none_of(
                    configuration->regions,
                    [scan_remaining = configuration->scan_remaining_area](
                        const ProcessingRegion& region)
                    {
                        return region.area_mode != 2U || scan_remaining;
                    }))
                return false;
            manual_reserved = true;
            reserved_manual_frame = latest_frame;
            return true;
        }

        void cancel_manual_reservation() noexcept
        {
            std::scoped_lock lock(gate);
            manual_reserved = false;
            reserved_manual_frame.reset();
        }

        void dispatch_reserved_manual() noexcept
        {
            std::shared_ptr<capture::captured_frame> frame;
            {
                std::scoped_lock lock(gate);
                if (!manual_reserved || !reserved_manual_frame) return;
                manual_reserved = false;
                manual_requested = true;
                frame = std::move(reserved_manual_frame);
            }

            // ProcessingState owns the worker and is itself owned by the target entry. The
            // controller cannot destroy either while this command is being applied, so a stopped
            // worker is only possible during process teardown. Keep the failure explicit without
            // turning a successfully reserved multi-target batch into a partial rejection.
            if (worker.submit(std::move(frame)) ==
                scheduling::latest_worker_submit_status::stopped)
            {
                std::scoped_lock lock(gate);
                manual_requested = false;
                ++processing_failures;
            }
        }

        [[nodiscard]] ThumbnailApplyResult make_thumbnail(
            const target_key& target_instance_id,
            const std::uint32_t maximum_long_edge) noexcept
        {
            ThumbnailApplyResult result{};
            result.target_instance_id = target_instance_id;
            std::shared_ptr<capture::captured_frame> frame;
            AdapterBinding binding{};
            capture::pixel_rect crop{};
            {
                std::scoped_lock lock(gate);
                frame = latest_frame;
                if (!frame)
                {
                    result.error_code = "thumbnail.frameUnavailable";
                    return result;
                }
                crop = target_crop.value_or(capture::pixel_rect{
                    0,
                    0,
                    static_cast<std::int32_t>(frame->width),
                    static_cast<std::int32_t>(frame->height)});
                const std::uint64_t adapter_key = capture::pack_adapter_luid(
                    frame->adapter_luid.HighPart,
                    frame->adapter_luid.LowPart);
                const auto found = bindings.find(adapter_key);
                if (found == bindings.end())
                {
                    result.error_code = "thumbnail.adapterUnavailable";
                    return result;
                }
                binding = found->second;
            }
            if (crop.left < 0 || crop.top < 0 ||
                crop.right <= crop.left || crop.bottom <= crop.top ||
                crop.right > static_cast<std::int32_t>(frame->width) ||
                crop.bottom > static_cast<std::int32_t>(frame->height))
            {
                result.error_code = "thumbnail.cropInvalid";
                return result;
            }

            ocr::texture_crop_readback_result readback = ocr::readback_texture_crop(
                binding.device,
                *binding.runtime,
                frame,
                {
                    static_cast<std::uint32_t>(crop.left),
                    static_cast<std::uint32_t>(crop.top),
                    static_cast<std::uint32_t>(crop.right - crop.left),
                    static_cast<std::uint32_t>(crop.bottom - crop.top),
                },
                67'108'864U,
                std::chrono::milliseconds(750));
            if (readback.status != ocr::texture_crop_readback_status::succeeded)
            {
                result.error_code = "thumbnail.readbackFailed";
                return result;
            }

            ocr::bgra_image scaled;
            try
            {
                scaled = ocr::downscale_bgra(readback.image, maximum_long_edge);
            }
            catch (...)
            {
                SecureZeroMemory(
                    readback.image.pixels.data(),
                    readback.image.pixels.size());
                result.error_code = "thumbnail.downscaleFailed";
                return result;
            }
            SecureZeroMemory(
                readback.image.pixels.data(),
                readback.image.pixels.size());
            const std::uint32_t width = scaled.width;
            const std::uint32_t height = scaled.height;
            ocr::cloud_crop_encode_result encoded =
                ocr::encode_png(scaled, 4U * 1024U * 1024U);
            SecureZeroMemory(scaled.pixels.data(), scaled.pixels.size());
            if (encoded.status != ocr::cloud_crop_encode_status::succeeded)
            {
                result.error_code = "thumbnail.encodeFailed";
                return result;
            }
            result.accepted = true;
            result.frame_sequence = frame->identity.frame_sequence;
            result.pixel_width = width;
            result.pixel_height = height;
            result.mime_type = "image/png";
            result.encoded_image = std::move(encoded.bytes);
            return result;
        }

        void store_background(
            const ProcessingRegion& region,
            const std::shared_ptr<const overlay::background_frame>& frame,
            const bool confirmed_clean) noexcept
        {
            if (!frame) return;
            std::scoped_lock lock(gate);
            if (region.area_mode != 0U) non_user_background_key = region.region_id;
            static_cast<void>(background_cache.update(
                region.region_id, frame, confirmed_clean));
        }

        [[nodiscard]] std::shared_ptr<const overlay::background_frame> background_for(
            const target_key& region_id,
            const overlay::rect_f bounds,
            const bool require_clean,
            const std::uint32_t target_width,
            const std::uint32_t target_height) noexcept
        {
            std::shared_ptr<const overlay::background_frame> frame;
            bool crop_from_target{};
            {
                std::scoped_lock lock(gate);
                frame = background_cache.snapshot(region_id, require_clean);
                if (!frame && non_user_background_key.has_value())
                {
                    frame = background_cache.snapshot(
                        *non_user_background_key, require_clean);
                    crop_from_target = frame != nullptr;
                }
            }
            if (!crop_from_target) return frame;
            return overlay::crop_background_frame(
                *frame,
                bounds,
                {0.0F, 0.0F, static_cast<float>(target_width),
                    static_cast<float>(target_height)},
                background_cache_bytes);
        }

        [[nodiscard]] bool should_process_signature(
            const target_key& region_id,
            const OcrExecutionIdentity& token,
            const imaging::change_signature& signature,
            const bool forced) noexcept
        {
            try
            {
                std::scoped_lock lock(gate);
                const auto state = ocr_regions.find(region_id);
                if (state == ocr_regions.end() ||
                    state->second.generation != token.source_generation ||
                    state->second.ocr_run_id != token.ocr_run_id)
                    return false;
                const auto& previous = state->second.last_dispatched_signature;
                const bool comparable = previous.has_value() &&
                    previous->columns == signature.columns &&
                    previous->rows == signature.rows;
                const bool changed = !comparable ||
                    imaging::meaningfully_changed(*previous, signature);
                if (!manual_ocr_allows_signature(forced, changed))
                {
                    state->second.outstanding_until = {};
                    ++unchanged_regions_skipped;
                    return false;
                }
                return true;
            }
            catch (...)
            {
                std::scoped_lock lock(gate);
                const auto state = ocr_regions.find(region_id);
                if (state != ocr_regions.end() &&
                    state->second.generation == token.source_generation)
                    state->second.outstanding_until = {};
                ++processing_failures;
                return false;
            }
        }

        void commit_signature(
            const target_key& region_id,
            const OcrExecutionIdentity& token,
            imaging::change_signature signature) noexcept
        {
            std::scoped_lock lock(gate);
            const auto state = ocr_regions.find(region_id);
            if (state != ocr_regions.end() &&
                state->second.generation == token.source_generation &&
                state->second.ocr_run_id == token.ocr_run_id)
                state->second.last_dispatched_signature = std::move(signature);
        }

        void process(std::shared_ptr<capture::captured_frame> frame) noexcept
        {
            const auto now = std::chrono::steady_clock::now();
            std::vector<ScheduledCloudRegion> scheduled;
            std::shared_ptr<const ProcessingConfigurationCommand> active_configuration;
            bool forced_manual{};
            capture::pixel_rect active_crop{
                0, 0,
                static_cast<std::int32_t>(frame->width),
                static_cast<std::int32_t>(frame->height)};
            {
                std::scoped_lock lock(gate);
                last_frame_sequence = frame->identity.frame_sequence;
                last_device_epoch = frame->identity.device_epoch;
                if (!configuration || !processing_supported)
                {
                    ++frames_without_configuration;
                    return;
                }
                active_configuration = configuration;
                if (target_crop.has_value()) active_crop = *target_crop;
                if (active_crop.left < 0 || active_crop.top < 0 ||
                    active_crop.right <= active_crop.left ||
                    active_crop.bottom <= active_crop.top ||
                    active_crop.right > static_cast<std::int32_t>(frame->width) ||
                    active_crop.bottom > static_cast<std::int32_t>(frame->height))
                {
                    ++processing_failures;
                    return;
                }
                const std::uint64_t adapter_key = capture::pack_adapter_luid(
                    frame->adapter_luid.HighPart, frame->adapter_luid.LowPart);
                const auto binding = bindings.find(adapter_key);
                if (binding == bindings.end())
                {
                    ++processing_failures;
                    return;
                }
                std::vector<ProcessingRegion> effective_regions;
                effective_regions.reserve(configuration->regions.size());
                for (const ProcessingRegion& region : configuration->regions)
                {
                    if (region.area_mode != 2U)
                    {
                        effective_regions.push_back(region);
                    }
                    else if (configuration->scan_remaining_area)
                    {
                        ProcessingRegion automatic = region;
                        automatic.x = 0.0;
                        automatic.y = 0.0;
                        automatic.width = 1.0;
                        automatic.height = 1.0;
                        automatic.recognition_interval_milliseconds =
                            configuration->remaining_area_interval_milliseconds;
                        effective_regions.push_back(std::move(automatic));
                    }
                }
                forced_manual = manual_requested;
                if (forced_manual) manual_requested = false;
                for (const ProcessingRegion& region : effective_regions)
                {
                    const auto configured_policy = policy_actions.find(region.region_id);
                    const std::uint8_t actions = configured_policy == policy_actions.end()
                        ? 0U
                        : configured_policy->second;
                    const bool policy_suppressed = (actions & 1U) != 0U ||
                        ((actions & (1U << 3U)) != 0U && region.area_mode == 2U);
                    if (!manual_ocr_allows_region(
                        forced_manual, policy_suppressed, true, false))
                        continue;
                    bool cadence_due = true;
                    if (!forced_manual)
                    {
                        auto [due, inserted] = next_due.try_emplace(region.region_id, now);
                        cadence_due = inserted || due->second <= now;
                        if (!cadence_due) continue;
                        std::uint64_t interval = region.recognition_interval_milliseconds;
                        if (((actions & (1U << 1U)) != 0U && region.priority >= 2U) ||
                            ((actions & (1U << 2U)) != 0U && region.area_mode == 2U))
                            interval = (std::min<std::uint64_t>)(
                                interval * 2U, 3'600'000U);
                        due->second = now + std::chrono::milliseconds(interval);
                    }
                    OcrRegionState& region_state = ocr_regions[region.region_id];
                    if (!manual_ocr_allows_region(
                        forced_manual,
                        policy_suppressed,
                        cadence_due,
                        region_state.outstanding_until > now))
                        continue;
                    ++scheduled_regions;
                    if (!nonzero_identity(region_state.text_track_id))
                    {
                        const auto identity = create_identity();
                        if (!identity.has_value())
                        {
                            ++processing_failures;
                            continue;
                        }
                        region_state.text_track_id = *identity;
                    }
                    const auto run_id = create_identity();
                    if (!run_id.has_value() ||
                        region_state.generation == static_cast<std::uint64_t>(INT64_MAX))
                    {
                        ++processing_failures;
                        continue;
                    }
                    ++region_state.generation;
                    region_state.outstanding_until = now + std::chrono::seconds(15);
                    OcrExecutionIdentity token{};
                    token.runtime_epoch = runtime_epoch;
                    token.target_instance_id = active_configuration->target_instance_id;
                    token.area_kind = region.area_mode;
                    token.manual = forced_manual;
                    if (region.area_mode == 0U) token.region_id = region.region_id;
                    token.text_track_id = region_state.text_track_id;
                    token.source_generation = region_state.generation;
                    token.profile_revision = active_configuration->profile_revision;
                    token.ocr_run_id = *run_id;
                    region_state.ocr_run_id = *run_id;
                    token.attempt = 1U;
                    token.result_sequence = 1U;
                    scheduled.push_back({region, token, binding->second, forced_manual});
                }
            }
            for (ScheduledCloudRegion& work : scheduled)
            {
                const auto fail = [&](const std::string_view error_code = {})
                {
                    {
                        std::scoped_lock lock(gate);
                        ++processing_failures;
                        auto state = ocr_regions.find(work.region.region_id);
                        if (state != ocr_regions.end() &&
                            state->second.generation == work.token.source_generation)
                            state->second.outstanding_until = {};
                    }
                    if (!error_code.empty() && ocr_result)
                    {
                        OcrResultCommand failure{};
                        failure.token = work.token;
                        failure.model_id = u"engine.processing";
                        failure.model_version = u"1";
                        failure.terminal_error_code.assign(
                            error_code.data(), error_code.size());
                        bool delivered{};
                        try { delivered = ocr_result(failure); }
                        catch (...) { delivered = false; }
                        if (delivered)
                        {
                            std::scoped_lock lock(gate);
                            ++local_results_sent;
                        }
                    }
                };
                const capture::pixel_rect mapped = capture::map_normalized_region_to_source(
                    active_crop,
                    work.region.x,
                    work.region.y,
                    work.region.width,
                    work.region.height);
                ocr::texture_crop_readback_result readback = ocr::readback_texture_crop(
                    work.binding.device,
                    *work.binding.runtime,
                    frame,
                    {
                        static_cast<std::uint32_t>(mapped.left),
                        static_cast<std::uint32_t>(mapped.top),
                        static_cast<std::uint32_t>(mapped.right - mapped.left),
                        static_cast<std::uint32_t>(mapped.bottom - mapped.top),
                    },
                    67'108'864U,
                    std::chrono::seconds(2));
                if (readback.status != ocr::texture_crop_readback_status::succeeded)
                {
                    fail("ocr.readback.failed");
                    continue;
                }
                imaging::change_signature signature;
                try
                {
                    signature = imaging::make_bgra_change_signature(
                        std::span<const std::uint8_t>(
                            reinterpret_cast<const std::uint8_t*>(
                                readback.image.pixels.data()),
                            readback.image.pixels.size()),
                        readback.image.width,
                        readback.image.height,
                        readback.image.stride);
                }
                catch (...)
                {
                    SecureZeroMemory(readback.image.pixels.data(),
                        readback.image.pixels.size());
                    fail("ocr.changeDetection.failed");
                    continue;
                }
                if (!should_process_signature(
                    work.region.region_id, work.token, signature, work.forced))
                {
                    SecureZeroMemory(readback.image.pixels.data(),
                        readback.image.pixels.size());
                    continue;
                }
                const std::uint32_t requested_edge = static_cast<std::uint32_t>((std::clamp)(
                    std::lround(active_configuration->detection_long_edge *
                        work.region.detection_scale), 1L, 1920L));
                ocr::bgra_image scaled;
                try { scaled = ocr::downscale_bgra(readback.image, requested_edge); }
                catch (...)
                {
                    SecureZeroMemory(readback.image.pixels.data(),
                        readback.image.pixels.size());
                    fail("ocr.downscale.failed");
                    continue;
                }
                SecureZeroMemory(readback.image.pixels.data(), readback.image.pixels.size());
                const auto background = overlay::make_background_frame(
                    scaled.pixels,
                    scaled.width,
                    scaled.height,
                    scaled.stride,
                    background_frame_bytes);
                store_background(work.region, background, false);
                if (work.region.area_mode == 2U)
                {
                    std::vector<ocr::normalized_mask_rect> exclusions;
                    exclusions.reserve(active_configuration->regions.size());
                    for (const ProcessingRegion& configured : active_configuration->regions)
                    {
                        if (configured.area_mode == 0U)
                            exclusions.push_back({
                                configured.x,
                                configured.y,
                                configured.width,
                                configured.height,
                            });
                    }
                    if (!ocr::mask_bgra_regions(scaled, exclusions))
                    {
                        SecureZeroMemory(scaled.pixels.data(), scaled.pixels.size());
                        fail("ocr.remainingAreaMask.failed");
                        continue;
                    }
                }
                const ocr::preprocessing_parse_result preprocessing =
                    ocr::parse_preprocessing_pipeline(work.region.preprocessing_pipeline);
                if (!preprocessing.valid ||
                    (preprocessing.enabled &&
                        !preprocess_crop(scaled, preprocessing.options)))
                {
                    SecureZeroMemory(scaled.pixels.data(), scaled.pixels.size());
                    fail("ocr.preprocessing.failed");
                    continue;
                }
                if (!work.region.use_cloud_ocr)
                {
                    const ocr::windows_media_ocr_result recognized =
                        ocr::windows_media_ocr{}.recognize(
                            scaled, work.region.recognition_language);
                    if (recognized.status == ocr::windows_media_ocr_status::succeeded &&
                        recognized.lines.empty())
                        store_background(work.region, background, true);
                    SecureZeroMemory(scaled.pixels.data(), scaled.pixels.size());
                    OcrResultCommand result{};
                    result.token = work.token;
                    result.stable = recognized.status ==
                        ocr::windows_media_ocr_status::succeeded;
                    result.model_id = u"windows.media.ocr";
                    result.model_version = u"windows-11";
                    result.terminal_error_code = recognized.error_code;
                    try
                    {
                        result.lines.reserve(recognized.lines.size());
                        for (const ocr::ocr_line& line : recognized.lines)
                        {
                            const winrt::hstring text = winrt::to_hstring(line.text);
                            result.lines.push_back({
                                std::u16string(
                                    reinterpret_cast<const char16_t*>(text.c_str()),
                                    text.size()),
                                line.bounds.x,
                                line.bounds.y,
                                line.bounds.width,
                                line.bounds.height,
                                line.confidence,
                                line.orientation_degrees,
                                line.vertical,
                            });
                        }
                    }
                    catch (...)
                    {
                        fail("ocr.result.encodingFailed");
                        continue;
                    }
                    bool sent{};
                    try { sent = ocr_result && ocr_result(result); }
                    catch (...) { sent = false; }
                    if (!sent)
                    {
                        fail();
                        continue;
                    }
                    {
                        std::scoped_lock lock(gate);
                        ++local_results_sent;
                        auto state = ocr_regions.find(work.region.region_id);
                        if (state != ocr_regions.end() &&
                            state->second.generation == work.token.source_generation)
                            state->second.outstanding_until = {};
                    }
                    commit_signature(work.region.region_id, work.token, std::move(signature));
                    continue;
                }

                ocr::cloud_crop_encode_result encoded = ocr::encode_png(scaled, 5'242'880U);
                const std::uint32_t scaled_width = scaled.width;
                const std::uint32_t scaled_height = scaled.height;
                SecureZeroMemory(scaled.pixels.data(), scaled.pixels.size());
                if (encoded.status != ocr::cloud_crop_encode_status::succeeded)
                {
                    fail("ocr.cloud.encodeFailed");
                    continue;
                }
                CloudOcrCropEvent event{
                    work.token,
                    "image/png",
                    work.region.ocr_provider_id,
                    std::move(encoded.bytes),
                    scaled_width,
                    scaled_height,
                    work.region.cloud_consent_policy_revision,
                    utc_datetime_ticks() + cloud_request_lifetime_ticks,
                    5'242'880U,
                };
                bool sent{};
                try { sent = cloud_ocr && cloud_ocr(event); } catch (...) { sent = false; }
                SecureZeroMemory(event.encoded_crop.data(), event.encoded_crop.size());
                if (!sent)
                {
                    fail();
                    continue;
                }
                {
                    std::scoped_lock lock(gate);
                    ++cloud_requests_sent;
                }
                commit_signature(work.region.region_id, work.token, std::move(signature));
            }
        }

        [[nodiscard]] bool accept(
            const OcrExecutionIdentity& token,
            const bool confirmed_clean) noexcept
        {
            std::scoped_lock lock(gate);
            const auto state = std::ranges::find_if(ocr_regions,
                [&](const auto& entry)
                {
                    return entry.second.text_track_id == token.text_track_id;
                });
            if (state != ocr_regions.end() &&
                state->second.generation == token.source_generation &&
                state->second.ocr_run_id == token.ocr_run_id &&
                state->second.outstanding_until != std::chrono::steady_clock::time_point{})
            {
                if (confirmed_clean)
                {
                    const auto latest = background_cache.snapshot(
                        state->first, false);
                    if (latest)
                        static_cast<void>(background_cache.update(
                            state->first, latest, true));
                }
                state->second.outstanding_until = {};
                return true;
            }
            return false;
        }
    };

    struct Target final
    {
        target_key target_id{};
        std::uint64_t revision{};
        std::shared_ptr<Status> status;
        std::vector<Subscription> subscriptions;
        std::uint64_t overlay_adapter_key{};
        RECT overlay_bounds{};
        HWND target_window{};
        std::shared_ptr<OverlayStatus> overlay_status;
        std::unique_ptr<overlay::overlay_runtime> overlay_runtime;
        std::uint64_t last_overlay_revision{};
        std::optional<ProcessingConfigurationCommand> processing_configuration;
        std::shared_ptr<ProcessingState> processing_state;
        std::map<OcrTrackKey, AcceptedOcrResult> accepted_ocr_results;
    };

    struct OverlayStatus final
    {
        std::mutex gate;
        std::condition_variable changed;
        std::uint64_t expected_revision{};
        std::optional<overlay::overlay_runtime_event> event;
    };

    std::unordered_map<std::uint64_t, std::unique_ptr<AdapterContext>> adapters;
    std::unordered_map<target_key, Target, TargetKeyHash> targets;
    std::unordered_map<target_key, std::uint64_t, TargetKeyHash> last_revisions;
    std::uint64_t next_logical_id{1U};
    std::uint64_t next_device_epoch{1U};
    bool apartment_initialized{};
    lifecycle_callback callback;
    target_key runtime_epoch{};
    cloud_ocr_callback cloud_ocr;
    ocr_result_callback ocr_result;
    struct PolicyVersion final
    {
        std::uint64_t revision{};
        std::uint64_t profile_revision{};
    };
    std::map<target_key, PolicyVersion> policy_versions;

    Implementation(
        lifecycle_callback value,
        const target_key epoch,
        cloud_ocr_callback cloud_callback,
        ocr_result_callback result_callback)
        : callback(std::move(value)),
          runtime_epoch(epoch),
          cloud_ocr(std::move(cloud_callback)),
          ocr_result(std::move(result_callback))
    {
        try
        {
            winrt::init_apartment(winrt::apartment_type::multi_threaded);
            apartment_initialized = true;
        }
        catch (...)
        {
        }
    }

    void notify(
        const std::shared_ptr<Status>& status,
        const std::uint32_t state,
        std::string error_code = {},
        const std::int32_t native_error_code = 0,
        const bool force = false) noexcept
    {
        status->state.store(state);
        if ((!status->active.load() && !force) || !callback) return;
        CaptureTargetLifecycleEvent event{
            status->target_id,
            status->target_instance_id,
            status->sequence.fetch_add(1U) + 1U,
            state,
            status->pixel_width.load(),
            status->pixel_height.load(),
            status->dpi.load(),
            std::move(error_code),
            native_error_code,
        };
        try { callback(event); } catch (...) { }
    }

    ~Implementation()
    {
        targets.clear();
        adapters.clear();
        if (apartment_initialized) winrt::uninit_apartment();
    }

    [[nodiscard]] AdapterContext& context_for(const AdapterAndMonitor& output)
    {
        const auto existing = adapters.find(output.adapter_key);
        if (existing != adapters.end()) return *existing->second;

        D3D_FEATURE_LEVEL selected_level{};
        constexpr std::array requested_levels{
            D3D_FEATURE_LEVEL_11_1,
            D3D_FEATURE_LEVEL_11_0,
        };
        auto context = std::make_unique<AdapterContext>();
        const HRESULT created = D3D11CreateDevice(
            output.adapter.Get(),
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            requested_levels.data(),
            static_cast<UINT>(requested_levels.size()),
            D3D11_SDK_VERSION,
            &context->device,
            &selected_level,
            nullptr);
        if (FAILED(created)) throw std::runtime_error("D3D11 device creation failed");
        const std::uint64_t device_epoch = next_device_epoch++;
        context->runtime = std::make_unique<imaging::device_runtime>(
            context->device.Get(),
            device_epoch,
            8U,
            std::chrono::milliseconds(250));
        ID3D11Device* const device = context->device.Get();
        imaging::device_runtime* const runtime = context->runtime.get();
        context->sessions = std::make_unique<capture::capture_session_manager>(
            [device, runtime, device_epoch](const capture::capture_source_descriptor descriptor)
            {
                return std::make_unique<capture::windows_capture_source>(
                    descriptor, device, *runtime, device_epoch);
            });
        auto [inserted, _] = adapters.emplace(output.adapter_key, std::move(context));
        return *inserted->second;
    }

    void detach(const Target& target) noexcept
    {
        for (const Subscription& subscription : target.subscriptions)
        {
            const auto context = adapters.find(subscription.adapter_key);
            if (context != adapters.end())
                static_cast<void>(context->second->sessions->detach(subscription.logical_id));
        }
    }

    [[nodiscard]] bool attach(
        const AdapterAndMonitor& output,
        const capture::capture_source_descriptor descriptor,
        const std::shared_ptr<Status>& status,
        const std::shared_ptr<ProcessingState>& processing_state,
        const bool update_dimensions,
        std::vector<Subscription>& subscriptions)
    {
        AdapterContext& context = context_for(output);
        processing_state->bind(
            output.adapter_key, context.device.Get(), context.runtime.get());
        const std::uint64_t logical_id = next_logical_id++;
        const capture::physical_source_identity identity{
            descriptor.kind == capture::capture_source_kind::window
                ? capture::physical_source_kind::window
                : capture::physical_source_kind::monitor,
            descriptor.key,
            output.adapter_key,
        };
        const bool attached = context.sessions->attach(
            logical_id,
            identity,
            descriptor,
            [status, processing_state, update_dimensions](
                const std::shared_ptr<capture::captured_frame> frame) noexcept
            {
                if (!frame) return;
                if (update_dimensions)
                {
                    status->pixel_width.store(frame->width);
                    status->pixel_height.store(frame->height);
                    status->dpi.store(frame->dpi);
                }
                processing_state->submit(frame);
            },
            [this, status](const capture::capture_lifecycle lifecycle) noexcept
            {
                notify(status, lifecycle_state(lifecycle));
            });
        if (!attached) return false;
        std::uint32_t expected_state = state_available;
        static_cast<void>(status->state.compare_exchange_strong(
            expected_state, state_running));
        subscriptions.push_back({output.adapter_key, logical_id});
        return true;
    }

    [[nodiscard]] CaptureTargetApplyResult apply(const CaptureTargetCommand& command)
    {
        if (!apartment_initialized)
            return {false, state_unsupported, "capture.runtimeInitializationFailed"};
        const target_key instance_id = command.target_instance_id;
        const auto last_revision = last_revisions.find(instance_id);
        if (last_revision != last_revisions.end() &&
            command.command_revision <= last_revision->second)
            return {false, state_waiting_for_match, "capture.staleRevision"};

        const auto existing = targets.find(instance_id);
        if (existing != targets.end() && existing->second.target_id != command.target_id)
            return {false, state_waiting_for_match, "capture.targetIdentityMismatch"};

        if (command.operation == CaptureTargetOperation::remove)
        {
            if (existing != targets.end())
            {
                notify(existing->second.status, state_closed, {}, 0, true);
                existing->second.status->active.store(false);
                detach(existing->second);
                targets.erase(existing);
            }
            last_revisions[instance_id] = command.command_revision;
            return {true, state_closed, {}};
        }

        constexpr std::size_t maximum_capture_targets = 8U;
        if (existing == targets.end() && !runtime_capacity_available(
                targets.size(), 1U, maximum_capture_targets))
            return {false, state_unsupported, "capture.targetCapacity"};

        const std::vector<AdapterAndMonitor> outputs = enumerate_outputs();
        std::vector<Subscription> subscriptions;
        std::uint64_t overlay_adapter_key{};
        RECT overlay_bounds{};
        HWND target_window{};
        auto status = std::make_shared<Status>();
        auto processing_state = std::make_shared<ProcessingState>(
            runtime_epoch,
            cloud_ocr,
            ocr_result,
            true);
        status->target_id = command.target_id;
        status->target_instance_id = command.target_instance_id;
        try
        {
            if (command.kind == CaptureTargetKind::window)
            {
                const auto window = reinterpret_cast<HWND>(
                    static_cast<std::uintptr_t>(command.native_handle));
                if (IsWindow(window) == FALSE)
                    return {false, state_closed, "capture.targetUnavailable"};
                const HMONITOR monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONULL);
                const auto output = std::ranges::find(outputs, monitor,
                    &AdapterAndMonitor::monitor);
                if (output == outputs.end())
                    return {false, state_unsupported, "capture.adapterUnavailable"};
                RECT client{};
                if (GetClientRect(window, &client) == FALSE ||
                    client.right <= client.left || client.bottom <= client.top)
                    return {false, state_closed, "capture.targetUnavailable"};
                POINT origin{};
                if (ClientToScreen(window, &origin) == FALSE)
                    return {false, state_closed, "capture.targetUnavailable"};
                overlay_adapter_key = output->adapter_key;
                overlay_bounds = {origin.x, origin.y,
                    origin.x + client.right, origin.y + client.bottom};
                target_window = window;
                status->pixel_width.store(static_cast<std::uint32_t>(client.right - client.left));
                status->pixel_height.store(static_cast<std::uint32_t>(client.bottom - client.top));
                status->dpi.store(GetDpiForWindow(window));
                const capture::capture_source_descriptor descriptor{
                    command.native_handle,
                    capture::capture_source_kind::window,
                    window,
                    nullptr,
                };
                const std::array identities{
                    capture::physical_source_identity{
                        capture::physical_source_kind::window,
                        descriptor.key,
                        output->adapter_key,
                    }};
                if (!can_attach_physical_sources(identities))
                    return {false, state_unsupported, "capture.sourceCapacity"};
                if (!attach(*output, descriptor, status, processing_state, true, subscriptions))
                    return {false, state_unsupported, "capture.attachFailed"};
            }
            else if (command.kind == CaptureTargetKind::monitor)
            {
                const auto monitor = reinterpret_cast<HMONITOR>(
                    static_cast<std::uintptr_t>(command.native_handle));
                const auto output = std::ranges::find(outputs, monitor,
                    &AdapterAndMonitor::monitor);
                if (output == outputs.end())
                    return {false, state_closed, "capture.targetUnavailable"};
                overlay_adapter_key = output->adapter_key;
                overlay_bounds = output->desktop_pixels;
                status->pixel_width.store(static_cast<std::uint32_t>(
                    output->desktop_pixels.right - output->desktop_pixels.left));
                status->pixel_height.store(static_cast<std::uint32_t>(
                    output->desktop_pixels.bottom - output->desktop_pixels.top));
                const capture::capture_source_descriptor descriptor{
                    command.native_handle,
                    capture::capture_source_kind::monitor,
                    nullptr,
                    monitor,
                };
                const std::array identities{
                    capture::physical_source_identity{
                        capture::physical_source_kind::monitor,
                        descriptor.key,
                        output->adapter_key,
                    }};
                if (!can_attach_physical_sources(identities))
                    return {false, state_unsupported, "capture.sourceCapacity"};
                if (!attach(*output, descriptor, status, processing_state, true, subscriptions))
                    return {false, state_unsupported, "capture.attachFailed"};
            }
            else
            {
                const std::int64_t right = static_cast<std::int64_t>(command.region_x) +
                    command.region_width;
                const std::int64_t bottom = static_cast<std::int64_t>(command.region_y) +
                    command.region_height;
                if (right > (std::numeric_limits<std::int32_t>::max)() ||
                    bottom > (std::numeric_limits<std::int32_t>::max)())
                    return {false, state_unsupported, "capture.regionOverflow"};
                const capture::pixel_rect requested{
                    command.region_x,
                    command.region_y,
                    static_cast<std::int32_t>(right),
                    static_cast<std::int32_t>(bottom),
                };
                overlay_bounds = {requested.left, requested.top, requested.right, requested.bottom};
                status->pixel_width.store(static_cast<std::uint32_t>(command.region_width));
                status->pixel_height.store(static_cast<std::uint32_t>(command.region_height));
                std::vector<capture::monitor_geometry> monitors;
                monitors.reserve(outputs.size());
                for (const AdapterAndMonitor& output : outputs)
                    monitors.push_back({
                        reinterpret_cast<std::uint64_t>(output.monitor),
                        {output.desktop_pixels.left, output.desktop_pixels.top,
                            output.desktop_pixels.right, output.desktop_pixels.bottom},
                        output.adapter_key,
                    });
                const auto pieces = capture::split_desktop_region(requested, monitors);
                if (pieces.empty())
                    return {false, state_closed, "capture.targetUnavailable"};
                std::vector<capture::physical_source_identity> identities;
                identities.reserve(pieces.size());
                for (const capture::desktop_region_piece& piece : pieces)
                    identities.push_back({
                        capture::physical_source_kind::monitor,
                        piece.monitor_key,
                        piece.adapter_key,
                    });
                if (!can_attach_physical_sources(identities))
                    return {false, state_unsupported, "capture.sourceCapacity"};
                processing_state->configure_desktop_crop(
                    pieces.size() == 1U
                        ? std::optional<capture::pixel_rect>{pieces.front().source_pixels}
                        : std::nullopt);
                overlay_adapter_key = pieces.front().adapter_key;
                for (const capture::desktop_region_piece& piece : pieces)
                {
                    const auto output = std::ranges::find(outputs,
                        reinterpret_cast<HMONITOR>(piece.monitor_key),
                        &AdapterAndMonitor::monitor);
                    if (output == outputs.end())
                        throw std::runtime_error("Monitor topology changed during attachment");
                    const capture::capture_source_descriptor descriptor{
                        piece.monitor_key,
                        capture::capture_source_kind::monitor,
                        nullptr,
                        output->monitor,
                    };
                    if (!attach(*output, descriptor, status, processing_state, false, subscriptions))
                        throw std::runtime_error("Desktop region attachment failed");
                }
            }
        }
        catch (...)
        {
            Target rollback{command.target_id, command.command_revision, status,
                std::move(subscriptions)};
            detach(rollback);
            throw;
        }

        Target replacement{
            command.target_id,
            command.command_revision,
            status,
            std::move(subscriptions),
            overlay_adapter_key,
            overlay_bounds,
            target_window,
            {},
            {},
            0U,
            {},
            std::move(processing_state),
        };
        if (existing != targets.end())
        {
            existing->second.status->active.store(false);
            detach(existing->second);
        }
        targets.insert_or_assign(instance_id, std::move(replacement));
        last_revisions[instance_id] = command.command_revision;
        status->active.store(true);
        notify(status, status->state.load(), {}, 0, true);
        return {true, status->state.load(), {}};
    }

    [[nodiscard]] OverlayApplyResult apply_overlay(
        const std::shared_ptr<const overlay::desired_state>& state)
    {
        constexpr std::uint32_t status_applied = 1U;
        constexpr std::uint32_t status_superseded = 2U;
        constexpr std::uint32_t status_invalid = 3U;
        constexpr std::uint32_t status_capture_exclusion = 4U;
        constexpr std::uint32_t status_device_failed = 5U;
        constexpr std::uint32_t status_stopped = 6U;
        constexpr std::uint32_t status_background_unavailable = 7U;
        if (!state)
            return {false, status_invalid, "overlay.invalidState"};
        const auto target = targets.find(state->target_instance);
        if (target == targets.end())
            return {false, status_invalid, "overlay.targetUnavailable"};
        Target& value = target->second;
        if (state->revision <= value.last_overlay_revision)
            return {false, status_superseded, "overlay.staleRevision"};
        const auto adapter = adapters.find(value.overlay_adapter_key);
        if (adapter == adapters.end())
            return {false, status_device_failed, "overlay.adapterUnavailable"};

        auto render_state = std::make_shared<overlay::desired_state>(*state);
        for (overlay::region& region : render_state->regions)
        {
            const bool temporal = region.style.background ==
                overlay::background_mode::temporal_cache;
            const bool captured_background_required = temporal ||
                region.style.background == overlay::background_mode::automatic_contrast ||
                (region.style.background == overlay::background_mode::translucent &&
                    region.style.blur_radius > 0.0F);
            if (!captured_background_required) continue;
            region.background_pixels = value.processing_state->background_for(
                region.id,
                region.bounds,
                temporal,
                value.status->pixel_width.load(),
                value.status->pixel_height.load());
            if (!region.background_pixels)
                return {false, status_background_unavailable,
                    temporal
                        ? "overlay.temporalBackgroundUnavailable"
                        : "overlay.capturedBackgroundUnavailable"};
            if (!temporal && region.style.blur_radius > 0.0F)
            {
                const auto blurred = value.processing_state->background_blurs.get(
                    region.background_pixels,
                    static_cast<std::uint32_t>((std::clamp)(
                        std::lround(region.style.blur_radius), 0L, 64L)),
                    ProcessingState::background_frame_bytes);
                if (!blurred)
                    return {false, status_background_unavailable,
                        "overlay.backgroundBlurFailed"};
                region.background_pixels = blurred;
            }
        }

        if (!value.overlay_runtime)
        {
            value.overlay_status = std::make_shared<OverlayStatus>();
            const std::weak_ptr weak_status = value.overlay_status;
            value.overlay_runtime = std::make_unique<overlay::overlay_runtime>(
                adapter->second->device.Get(), value.overlay_bounds,
                [weak_status](const overlay::overlay_runtime_event event) noexcept
                {
                    const auto status = weak_status.lock();
                    if (!status) return;
                    {
                        std::scoped_lock lock(status->gate);
                        if (event.revision != 0U &&
                            event.revision != status->expected_revision) return;
                        status->event = event;
                    }
                    status->changed.notify_all();
                },
                value.target_window == nullptr
                    ? overlay::overlay_runtime::target_state_provider{}
                    : overlay::overlay_runtime::target_state_provider{
                        [window = value.target_window]() noexcept
                        {
                            return observe_window_target(window);
                        }});
            if (!value.overlay_runtime->start())
            {
                std::scoped_lock lock(value.overlay_status->gate);
                const auto initial = value.overlay_status->event;
                value.overlay_runtime.reset();
                value.overlay_status.reset();
                if (initial && initial->status ==
                    overlay::overlay_runtime_status::capture_exclusion_failed)
                    return {false, status_capture_exclusion,
                        "overlay.captureExclusionFailed"};
                return {false, status_device_failed, "overlay.initializationFailed"};
            }
        }

        if (value.target_window != nullptr)
        {
            RECT client{};
            POINT origin{};
            if (IsWindow(value.target_window) == FALSE ||
                GetClientRect(value.target_window, &client) == FALSE ||
                ClientToScreen(value.target_window, &origin) == FALSE ||
                client.right <= client.left || client.bottom <= client.top)
                return {false, status_invalid, "overlay.targetUnavailable"};
            value.overlay_bounds = {origin.x, origin.y,
                origin.x + client.right, origin.y + client.bottom};
        }
        const overlay::target_visibility visibility{
            value.target_window == nullptr || IsWindowVisible(value.target_window) != FALSE,
            value.target_window != nullptr && IsIconic(value.target_window) != FALSE,
            false,
            true,
        };
        if (!value.overlay_runtime->update_target(value.overlay_bounds, visibility))
            return {false, status_invalid, "overlay.targetUpdateFailed"};

        const auto status = value.overlay_status;
        {
            std::scoped_lock lock(status->gate);
            status->expected_revision = state->revision;
            status->event.reset();
        }
        if (!value.overlay_runtime->submit(render_state))
            return {false, status_superseded, "overlay.staleRevision"};
        value.last_overlay_revision = state->revision;

        std::unique_lock lock(status->gate);
        if (!status->changed.wait_for(lock, std::chrono::seconds(5), [&]
            {
                return status->event.has_value() &&
                    status->event->revision == state->revision;
            }))
            return {false, status_device_failed, "overlay.applyTimeout"};
        const overlay::overlay_runtime_event event = *status->event;
        switch (event.status)
        {
        case overlay::overlay_runtime_status::applied:
            return {true, status_applied, {}};
        case overlay::overlay_runtime_status::superseded:
            return {false, status_superseded, "overlay.superseded"};
        case overlay::overlay_runtime_status::invalid_state:
            return {false, status_invalid, "overlay.invalidState"};
        case overlay::overlay_runtime_status::capture_exclusion_failed:
            return {false, status_capture_exclusion, "overlay.captureExclusionFailed"};
        case overlay::overlay_runtime_status::device_failed:
            return {false, status_device_failed, "overlay.deviceFailed"};
        case overlay::overlay_runtime_status::background_unavailable:
            return {false, status_background_unavailable,
                "overlay.capturedBackgroundUnavailable"};
        case overlay::overlay_runtime_status::stopped:
            return {false, status_stopped, "overlay.stopped"};
        }
        return {false, status_invalid, "overlay.unknownStatus"};
    }

    [[nodiscard]] bool contains_target(const target_key& target_instance_id) const noexcept
    {
        return targets.contains(target_instance_id);
    }

    [[nodiscard]] ManualOcrApplyResult request_manual_ocr(
        const ManualOcrRequest& request)
    {
        constexpr std::uint8_t scheduled = 1U;
        constexpr std::uint8_t no_targets = 2U;
        constexpr std::uint8_t no_regions = 3U;
        constexpr std::uint8_t busy = 4U;
        constexpr std::uint8_t runtime_failure = 5U;
        constexpr std::uint8_t target_unavailable = 6U;
        const bool explicit_targets = request.scope == ManualOcrScope::explicit_targets;
        if ((request.scope != ManualOcrScope::all_targets && !explicit_targets) ||
            (request.scope == ManualOcrScope::all_targets &&
                !request.target_instance_ids.empty()) ||
            (explicit_targets && request.target_instance_ids.empty()))
            return {false, runtime_failure, 0U, 0U, "ocr.manual.invalidScope"};
        if (targets.empty() && !explicit_targets)
            return {false, no_targets, 0U, 0U, "ocr.manual.noTargets"};

        std::vector<Target*> requested;
        if (explicit_targets)
        {
            requested.reserve(request.target_instance_ids.size());
            for (const target_key& target_instance_id : request.target_instance_ids)
            {
                const auto target = targets.find(target_instance_id);
                if (target == targets.end())
                    return {false, target_unavailable, 0U, 0U, "ocr.manual.noMatch"};
                requested.push_back(&target->second);
            }
        }
        else
        {
            requested.reserve(targets.size());
            for (auto& [_, target] : targets)
                requested.push_back(&target);
        }

        std::vector<std::pair<ProcessingState*, std::size_t>> eligible;
        bool found_regions{};
        eligible.reserve(requested.size());
        for (Target* target : requested)
        {
            const std::size_t target_regions =
                target->processing_state->manual_region_count();
            const bool available = manual_ocr_target_available(
                target->status->state.load(),
                target->processing_state->manual_frame_available());
            if (explicit_targets && !available)
                return {false, target_unavailable, 0U, 0U,
                    "ocr.manual.targetUnavailable"};
            if (target_regions == 0U) continue;
            found_regions = true;
            if (!available)
                continue;
            if (target->processing_state->manual_request_busy())
                return {false, busy, 0U, 0U, "ocr.manual.busy"};
            eligible.emplace_back(target->processing_state.get(), target_regions);
        }
        if (eligible.empty())
        {
            if (!found_regions)
                return {false, no_regions, 0U, 0U, "ocr.manual.noRegions"};
            return {false, target_unavailable, 0U, 0U,
                "ocr.manual.targetUnavailable"};
        }

        std::vector<ProcessingState*> reserved;
        reserved.reserve(eligible.size());
        for (const auto& [state, target_regions] : eligible)
        {
            static_cast<void>(target_regions);
            if (!state->reserve_manual())
            {
                for (ProcessingState* const reserved_state : reserved)
                    reserved_state->cancel_manual_reservation();
                return {false, runtime_failure, 0U, 0U,
                    "ocr.manual.scheduleFailed"};
            }
            reserved.push_back(state);
        }

        // All target states are reserved before any worker can observe the manual request. From
        // this point the command is committed and every target is dispatched; failures before this
        // point roll back every reservation above.
        for (ProcessingState* const state : reserved)
            state->dispatch_reserved_manual();

        const std::size_t scheduled_regions = std::accumulate(
            eligible.begin(),
            eligible.end(),
            std::size_t{},
            [](const std::size_t total, const auto& entry)
            {
                return total + entry.second;
            });
        return {
            true,
            scheduled,
            static_cast<std::uint32_t>(eligible.size()),
            static_cast<std::uint32_t>(scheduled_regions),
            {},
        };
    }

    [[nodiscard]] ThumbnailApplyResult request_thumbnail(
        const ThumbnailRequest& request)
    {
        const auto target = targets.find(request.target_instance_id);
        if (target == targets.end())
        {
            ThumbnailApplyResult result{};
            result.target_instance_id = request.target_instance_id;
            result.error_code = "thumbnail.targetUnavailable";
            return result;
        }
        return target->second.processing_state->make_thumbnail(
            request.target_instance_id,
            request.maximum_long_edge);
    }

    [[nodiscard]] std::size_t target_count() const noexcept
    {
        return targets.size();
    }

    [[nodiscard]] std::size_t capture_source_count() const noexcept
    {
        std::size_t count{};
        for (const auto& [_, adapter] : adapters)
            count += adapter->sessions->physical_source_count();
        return count;
    }

    [[nodiscard]] bool can_attach_physical_sources(
        const std::span<const capture::physical_source_identity> identities) const noexcept
    {
        constexpr std::size_t maximum_capture_sources = 8U;
        std::unordered_set<capture::physical_source_identity,
            capture::physical_source_identity_hash> unique;
        std::size_t additional{};
        for (const capture::physical_source_identity identity : identities)
        {
            if (!unique.insert(identity).second) continue;
            const auto context = adapters.find(identity.adapter_key);
            if (context != adapters.end() &&
                context->second->sessions->logical_target_count(identity) != 0U)
                continue;
            ++additional;
        }
        return runtime_capacity_available(
            capture_source_count(), additional, maximum_capture_sources);
    }

    [[nodiscard]] bool try_get_gpu_budgets(
        std::vector<AdapterGpuBudget>& result) const noexcept
    {
        try
        {
            std::vector<AdapterGpuBudget> measured;
            measured.reserve(adapters.size());
            for (const auto& [adapter_key, context] : adapters)
            {
                ComPtr<IDXGIDevice> dxgi_device;
                ComPtr<IDXGIAdapter> adapter;
                ComPtr<IDXGIAdapter3> adapter3;
                DXGI_QUERY_VIDEO_MEMORY_INFO memory{};
                if (FAILED(context->device.As(&dxgi_device)) ||
                    FAILED(dxgi_device->GetAdapter(&adapter)) ||
                    FAILED(adapter.As(&adapter3)) ||
                    FAILED(adapter3->QueryVideoMemoryInfo(
                        0U, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &memory)))
                    return false;
                const std::uint64_t limit = calculate_gpu_pool_limit(memory.Budget);
                if (limit == 0U || memory.CurrentUsage > limit)
                    return false;
                measured.push_back({adapter_key, limit, memory.CurrentUsage});
            }
            std::ranges::sort(measured, {}, &AdapterGpuBudget::adapter_key);
            result = std::move(measured);
            return true;
        }
        catch (...)
        {
            return false;
        }
    }

    [[nodiscard]] ProcessingApplyResult apply_processing(
        ProcessingConfigurationCommand command)
    {
        constexpr std::uint8_t applied = 1U;
        constexpr std::uint8_t stale_revision = 2U;
        constexpr std::uint8_t stale_profile = 3U;
        constexpr std::uint8_t target_missing = 4U;
        const auto target = targets.find(command.target_instance_id);
        if (target == targets.end())
            return {false, target_missing, "runtime.processing.targetMissing"};
        const std::size_t remaining_templates = static_cast<std::size_t>(
            std::ranges::count_if(command.regions,
                [](const ProcessingRegion& region) { return region.area_mode == 2U; }));
        if ((command.scan_remaining_area && remaining_templates != 1U) ||
            (!command.scan_remaining_area && remaining_templates != 0U))
            return {false, 5U, "runtime.processing.remainingAreaTemplateInvalid"};
        if (std::ranges::any_of(command.regions, [](const ProcessingRegion& region)
            {
                return !ocr::parse_preprocessing_pipeline(
                    region.preprocessing_pipeline).valid;
            }))
            return {false, 5U, "runtime.processing.preprocessingInvalid"};
        if (!target->second.processing_state->processing_supported)
            return {false, 5U, "runtime.processing.desktopRegionCompositionUnavailable"};
        const auto& current = target->second.processing_configuration;
        if (current.has_value())
        {
            if (command.configuration_revision <= current->configuration_revision)
                return {false, stale_revision, "runtime.processing.staleRevision"};
            if (command.profile_id == current->profile_id &&
                command.profile_revision < current->profile_revision)
                return {false, stale_profile, "runtime.processing.staleProfile"};
        }
        if (!current.has_value() || command.profile_id != current->profile_id ||
            command.profile_revision != current->profile_revision)
            target->second.accepted_ocr_results.clear();
        target->second.processing_configuration = std::move(command);
        target->second.processing_state->apply(
            *target->second.processing_configuration);
        return {true, applied, {}};
    }

    [[nodiscard]] OcrResultApplyResult apply_ocr_result(
        const OcrResultCommand& command)
    {
        constexpr std::uint32_t applied = 1U;
        constexpr std::uint32_t stale_profile = 2U;
        constexpr std::uint32_t stale_generation = 3U;
        constexpr std::uint32_t target_missing = 4U;
        constexpr std::uint32_t invalid_area = 5U;
        const auto target = targets.find(command.token.target_instance_id);
        if (target == targets.end())
            return {false, target_missing, "ocr.result.targetMissing"};
        Target& value = target->second;
        if (!value.processing_configuration.has_value() ||
            command.token.profile_revision !=
                value.processing_configuration->profile_revision)
            return {false, stale_profile, "ocr.result.staleProfile"};

        const ProcessingConfigurationCommand& configuration =
            *value.processing_configuration;
        const bool area_exists = command.token.area_kind == 0U
            ? std::ranges::any_of(configuration.regions,
                [&](const ProcessingRegion& region)
                {
                    return region.region_id == command.token.region_id;
                })
            : command.token.area_kind == 1U
                ? std::ranges::any_of(configuration.regions,
                    [](const ProcessingRegion& region) { return region.area_mode == 1U; })
                : configuration.scan_remaining_area ||
                    std::ranges::any_of(configuration.regions,
                        [](const ProcessingRegion& region) { return region.area_mode == 2U; });
        if (!area_exists)
            return {false, invalid_area, "ocr.result.invalidArea"};
        if (!value.processing_state->accept(
                command.token,
                command.stable && command.lines.empty() &&
                    command.terminal_error_code.empty()))
            return {false, stale_generation, "ocr.result.unknownExecution"};

        const OcrTrackKey key{
            command.token.area_kind,
            command.token.region_id,
            command.token.text_track_id,
        };
        const auto current = value.accepted_ocr_results.find(key);
        if (current != value.accepted_ocr_results.end() &&
            (command.token.source_generation < current->second.source_generation ||
                (command.token.source_generation == current->second.source_generation &&
                    command.token.result_sequence <= current->second.result_sequence)))
            return {false, stale_generation, "ocr.result.staleGeneration"};
        value.accepted_ocr_results.insert_or_assign(key, AcceptedOcrResult{
            command.token.source_generation,
            command.token.result_sequence,
        });
        return {true, applied, {}};
    }

    [[nodiscard]] std::string apply_policy(const PolicyRevisionCommand& command)
    {
        const auto current_version = policy_versions.find(command.profile_id);
        if (current_version != policy_versions.end())
        {
            if (command.revision <= current_version->second.revision)
                return "runtime.policy.staleRevision";
            if (command.profile_revision < current_version->second.profile_revision)
                return "runtime.policy.staleProfile";
        }

        std::map<target_key, std::uint8_t> requested;
        for (const PolicyRegionState& region : command.regions)
        {
            std::uint8_t actions{};
            std::size_t offset{};
            while (offset < region.policy.size())
            {
                const std::size_t end = region.policy.find(';', offset);
                const std::size_t length = end == std::string::npos
                    ? region.policy.size() - offset
                    : end - offset;
                const std::string_view item(region.policy.data() + offset, length);
                if (item.size() < 3U || item[1U] != ':' || item.front() < '0' ||
                    item.front() > '5')
                    return "runtime.policy.invalidAction";
                const std::uint8_t action = static_cast<std::uint8_t>(item.front() - '0');
                const std::string_view state = item.substr(2U);
                if (action > 3U && state != "configured")
                    return "runtime.policy.actionUnsupported";
                if (state == "degraded" || state == "paused")
                    actions = static_cast<std::uint8_t>(actions | (1U << action));
                else if (state != "configured")
                    return "runtime.policy.invalidState";
                if (end == std::string::npos) break;
                if (end + 1U == region.policy.size())
                    return "runtime.policy.invalidAction";
                offset = end + 1U;
            }
            requested.emplace(region.region_id, actions);
        }

        bool matched_profile{};
        std::map<ProcessingState*, std::map<target_key, std::uint8_t>> per_target;
        std::set<target_key> matched_regions;
        for (auto& [_, target] : targets)
        {
            if (!target.processing_configuration.has_value() ||
                target.processing_configuration->profile_id != command.profile_id ||
                target.processing_configuration->profile_revision != command.profile_revision)
                continue;
            matched_profile = true;
            auto& actions = per_target[target.processing_state.get()];
            for (const ProcessingRegion& region : target.processing_configuration->regions)
            {
                const auto requested_region = requested.find(region.region_id);
                if (requested_region == requested.end()) continue;
                if (region.lock_degradation && requested_region->second != 0U &&
                    (requested_region->second & 1U) == 0U)
                    return "runtime.policy.regionLocked";
                actions.emplace(region.region_id, requested_region->second);
                matched_regions.insert(region.region_id);
            }
        }
        if (!matched_profile) return "runtime.policy.profileUnavailable";
        if (matched_regions.size() != requested.size())
            return "runtime.policy.regionUnavailable";
        for (auto& [state, actions] : per_target)
            state->apply_policy(std::move(actions));
        policy_versions.insert_or_assign(command.profile_id, PolicyVersion{
            command.revision,
            command.profile_revision,
        });
        return {};
    }
};

RuntimeCaptureController::RuntimeCaptureController(
    lifecycle_callback callback,
    const std::array<std::byte, 16U> runtime_epoch,
    cloud_ocr_callback cloud_ocr,
    ocr_result_callback ocr_result)
    : implementation_(std::make_unique<Implementation>(
        std::move(callback), runtime_epoch, std::move(cloud_ocr),
        std::move(ocr_result)))
{
}

RuntimeCaptureController::~RuntimeCaptureController() = default;

CaptureTargetApplyResult RuntimeCaptureController::apply(
    const CaptureTargetCommand& command) noexcept
{
    try
    {
        return implementation_->apply(command);
    }
    catch (const winrt::hresult_access_denied&)
    {
        return {false, state_unsupported, "capture.accessDenied",
            static_cast<std::int32_t>(E_ACCESSDENIED)};
    }
    catch (const winrt::hresult_invalid_argument&)
    {
        return {false, state_unsupported, "capture.invalidTarget",
            static_cast<std::int32_t>(E_INVALIDARG)};
    }
    catch (const winrt::hresult_not_implemented&)
    {
        return {false, state_unsupported, "capture.notSupported",
            static_cast<std::int32_t>(E_NOTIMPL)};
    }
    catch (const winrt::hresult_error& error)
    {
        const HRESULT code = error.code();
        if (code == HRESULT_FROM_WIN32(ERROR_SERVICE_DOES_NOT_EXIST) ||
            code == HRESULT_FROM_WIN32(ERROR_SERVICE_DISABLED))
            return {false, state_unsupported, "capture.serviceUnavailable",
                static_cast<std::int32_t>(code)};
        if (code == DXGI_ERROR_DEVICE_REMOVED || code == DXGI_ERROR_DEVICE_RESET ||
            code == DXGI_ERROR_DEVICE_HUNG)
            return {false, state_device_lost, "capture.deviceLost",
                static_cast<std::int32_t>(code)};
        return {false, state_unsupported, "capture.wgcFailure",
            static_cast<std::int32_t>(code)};
    }
    catch (const std::runtime_error&)
    {
        return {false, state_unsupported, "capture.platformFailure"};
    }
    catch (...)
    {
        return {false, state_unsupported, "capture.runtimeFailure"};
    }
}

OverlayApplyResult RuntimeCaptureController::apply_overlay(
    std::shared_ptr<const overlay::desired_state> state) noexcept
{
    try
    {
        return implementation_->apply_overlay(state);
    }
    catch (const std::bad_alloc&)
    {
        return {false, 5U, "overlay.outOfMemory",
            static_cast<std::int32_t>(E_OUTOFMEMORY)};
    }
    catch (const std::exception&)
    {
        return {false, 5U, "overlay.runtimeFailure"};
    }
    catch (...)
    {
        return {false, 5U, "overlay.runtimeFailure"};
    }
}

bool RuntimeCaptureController::contains_target(
    const std::array<std::byte, 16U>& target_instance_id) const noexcept
{
    return implementation_->contains_target(target_instance_id);
}

ProcessingApplyResult RuntimeCaptureController::apply_processing(
    ProcessingConfigurationCommand command) noexcept
{
    try
    {
        return implementation_->apply_processing(std::move(command));
    }
    catch (const std::bad_alloc&)
    {
        return {false, 5U, "runtime.processing.outOfMemory"};
    }
    catch (...)
    {
        return {false, 5U, "runtime.processing.runtimeFailure"};
    }
}

std::string RuntimeCaptureController::apply_policy(
    const PolicyRevisionCommand& command) noexcept
{
    try { return implementation_->apply_policy(command); }
    catch (...) { return "runtime.policy.applyFailed"; }
}

OcrResultApplyResult RuntimeCaptureController::apply_ocr_result(
    const OcrResultCommand& command) noexcept
{
    try
    {
        return implementation_->apply_ocr_result(command);
    }
    catch (const std::bad_alloc&)
    {
        return {false, 6U, "ocr.result.outOfMemory"};
    }
    catch (...)
    {
        return {false, 6U, "ocr.result.runtimeFailure"};
    }
}

ManualOcrApplyResult RuntimeCaptureController::request_manual_ocr(
    const ManualOcrRequest& request) noexcept
{
    try
    {
        return implementation_->request_manual_ocr(request);
    }
    catch (const std::bad_alloc&)
    {
        return {false, 5U, 0U, 0U, "ocr.manual.outOfMemory"};
    }
    catch (...)
    {
        return {false, 5U, 0U, 0U, "ocr.manual.runtimeFailure"};
    }
}

ThumbnailApplyResult RuntimeCaptureController::request_thumbnail(
    const ThumbnailRequest& request) noexcept
{
    try
    {
        return implementation_->request_thumbnail(request);
    }
    catch (const std::bad_alloc&)
    {
        ThumbnailApplyResult result{};
        result.target_instance_id = request.target_instance_id;
        result.error_code = "thumbnail.outOfMemory";
        return result;
    }
    catch (...)
    {
        ThumbnailApplyResult result{};
        result.target_instance_id = request.target_instance_id;
        result.error_code = "thumbnail.runtimeFailure";
        return result;
    }
}

std::size_t RuntimeCaptureController::target_count() const noexcept
{
    return implementation_->target_count();
}

std::size_t RuntimeCaptureController::capture_source_count() const noexcept
{
    return implementation_->capture_source_count();
}

bool RuntimeCaptureController::try_get_gpu_budgets(
    std::vector<AdapterGpuBudget>& budgets) const noexcept
{
    return implementation_->try_get_gpu_budgets(budgets);
}
}
