#include "infini/capture/windows_capture_source.hpp"
#include "infini/imaging/device_runtime.hpp"

#ifdef _WIN32

#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <winrt/base.h>

#include <atomic>
#include <mutex>
#include <stdexcept>
#include <utility>

namespace infini::capture {
namespace {

using namespace winrt::Windows::Graphics::Capture;
using namespace winrt::Windows::Graphics::DirectX;
using namespace winrt::Windows::Graphics::DirectX::Direct3D11;
using dxgi_interface_access =
    ::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess;

[[nodiscard]] GraphicsCaptureItem create_item(const capture_source_descriptor& descriptor) {
    const auto interop = winrt::get_activation_factory<GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
    GraphicsCaptureItem item{nullptr};
    winrt::check_hresult(descriptor.kind == capture_source_kind::window
        ? interop->CreateForWindow(
            descriptor.window,
            winrt::guid_of<GraphicsCaptureItem>(),
            winrt::put_abi(item))
        : interop->CreateForMonitor(
            descriptor.monitor,
            winrt::guid_of<GraphicsCaptureItem>(),
            winrt::put_abi(item)));
    return item;
}

[[nodiscard]] IDirect3DDevice create_direct3d_device(ID3D11Device* device) {
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgi_device;
    winrt::check_hresult(device->QueryInterface(IID_PPV_ARGS(&dxgi_device)));
    winrt::com_ptr<::IInspectable> inspectable;
    winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi_device.Get(), inspectable.put()));
    return inspectable.as<IDirect3DDevice>();
}

[[nodiscard]] LUID adapter_luid(ID3D11Device* device) {
    Microsoft::WRL::ComPtr<IDXGIDevice> dxgi_device;
    winrt::check_hresult(device->QueryInterface(IID_PPV_ARGS(&dxgi_device)));
    Microsoft::WRL::ComPtr<IDXGIAdapter> adapter;
    winrt::check_hresult(dxgi_device->GetAdapter(&adapter));
    DXGI_ADAPTER_DESC description{};
    winrt::check_hresult(adapter->GetDesc(&description));
    return description.AdapterLuid;
}

[[nodiscard]] std::int64_t qpc_now() noexcept {
    LARGE_INTEGER value{};
    QueryPerformanceCounter(&value);
    return value.QuadPart;
}

} // namespace

struct windows_capture_source::implementation final : std::enable_shared_from_this<implementation> {
    capture_source_descriptor descriptor{};
    Microsoft::WRL::ComPtr<ID3D11Device> d3d_device;
    imaging::device_runtime* device_runtime{};
    std::uint64_t device_epoch{};
    IDirect3DDevice direct3d_device{nullptr};
    GraphicsCaptureItem item{nullptr};
    Direct3D11CaptureFramePool frame_pool{nullptr};
    GraphicsCaptureSession session{nullptr};
    winrt::event_token frame_arrived_token{};
    frame_callback on_frame;
    lifecycle_callback on_lifecycle;
    std::shared_ptr<frame_lease> latest_lease;
    std::mutex gate;
    std::atomic<bool> is_running{};
    std::atomic<bool> recreating{};
    std::atomic<std::uint64_t> latest_requested_sequence{};
    std::atomic<std::uint64_t> sequence{};
    std::atomic<std::uint64_t> lease_sequence{};
    std::uint32_t last_dpi{96U};
    winrt::Windows::Graphics::SizeInt32 pool_size{};
    LUID luid{};

    void notify_lifecycle(const capture_lifecycle lifecycle) noexcept {
        lifecycle_callback callback;
        {
            std::scoped_lock lock(gate);
            if (!is_running.load()) return;
            callback = on_lifecycle;
        }
        if (callback) callback(lifecycle);
    }

    void emit_frame(std::shared_ptr<captured_frame> frame) noexcept {
        frame_callback callback;
        {
            std::scoped_lock lock(gate);
            if (!is_running.load()) return;
            callback = on_frame;
        }
        if (callback) callback(std::move(frame));
    }

    void frame_arrived(Direct3D11CaptureFramePool const& sender) noexcept {
        try {
            if (!is_running.load()) return;
            Direct3D11CaptureFrame frame = sender.TryGetNextFrame();
            if (!frame) return;
            if (descriptor.kind == capture_source_kind::window && !IsWindow(descriptor.window)) {
                frame.Close();
                notify_lifecycle(capture_lifecycle::closed);
                return;
            }
            const auto size = frame.ContentSize();
            if (size.Width <= 0 || size.Height <= 0) {
                frame.Close();
                notify_lifecycle(capture_lifecycle::minimized);
                return;
            }
            if (recreating.load()) {
                frame.Close();
                return;
            }
            const std::uint64_t current_sequence = sequence.fetch_add(1U) + 1U;
            latest_requested_sequence.store(current_sequence);
            const frame_identity identity{
                descriptor.key,
                current_sequence,
                device_epoch,
                lease_sequence.fetch_add(1U) + 1U,
            };
            if (size.Width != pool_size.Width || size.Height != pool_size.Height) {
                recreating.store(true);
                auto resize_lease = std::make_shared<frame_lease>(identity, [owned_frame = frame]() mutable {
                    owned_frame.Close();
                });
                auto resize_ticket = resize_lease->acquire_ticket();
                const auto requested_size = size;
                const auto weak_state = weak_from_this();
                const bool queued = device_runtime->enqueue(
                    std::move(resize_ticket),
                    [](ID3D11DeviceContext*) { return S_OK; },
                    [weak_state, resize_lease, requested_size](const imaging::device_submission_result result) {
                        resize_lease->release_root();
                        const auto state = weak_state.lock();
                        if (!state) return;
                        try {
                            if (result.status == imaging::device_submission_status::completed &&
                                state->is_running.load()) {
                                state->frame_pool.Recreate(
                                    state->direct3d_device,
                                    DirectXPixelFormat::B8G8R8A8UIntNormalized,
                                    3,
                                    requested_size);
                                state->pool_size = requested_size;
                                state->notify_lifecycle(capture_lifecycle::resized);
                            } else {
                                state->notify_lifecycle(result.status == imaging::device_submission_status::device_lost
                                    ? capture_lifecycle::device_lost
                                    : capture_lifecycle::unsupported);
                            }
                        } catch (...) {
                            state->notify_lifecycle(capture_lifecycle::unsupported);
                        }
                        state->recreating.store(false);
                    });
                resize_lease->release_root();
                if (!queued) {
                    recreating.store(false);
                    notify_lifecycle(capture_lifecycle::unsupported);
                }
                return;
            }
            Microsoft::WRL::ComPtr<ID3D11Texture2D> source_texture;
            const auto access = frame.Surface().as<dxgi_interface_access>();
            winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(&source_texture)));
            D3D11_TEXTURE2D_DESC texture_description{};
            source_texture->GetDesc(&texture_description);
            texture_description.Width = static_cast<UINT>(size.Width);
            texture_description.Height = static_cast<UINT>(size.Height);
            texture_description.MipLevels = 1;
            texture_description.ArraySize = 1;
            texture_description.Usage = D3D11_USAGE_DEFAULT;
            texture_description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
            texture_description.CPUAccessFlags = 0;
            texture_description.MiscFlags = 0;
            Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
            winrt::check_hresult(d3d_device->CreateTexture2D(
                &texture_description,
                nullptr,
                &texture));
            const std::uint32_t dpi = descriptor.kind == capture_source_kind::window
                ? GetDpiForWindow(descriptor.window)
                : 96U;
            const auto timestamp = qpc_now();
            auto copy_lease = std::make_shared<frame_lease>(identity, [owned_frame = frame]() mutable {
                owned_frame.Close();
            });
            auto copy_ticket = copy_lease->acquire_ticket();
            const auto weak_state = weak_from_this();
            const bool queued = device_runtime->enqueue(
                std::move(copy_ticket),
                [source_texture, texture](ID3D11DeviceContext* context) {
                    context->CopyResource(texture.Get(), source_texture.Get());
                    return S_OK;
                },
                [weak_state, copy_lease, identity, size, dpi, timestamp, texture]
                (const imaging::device_submission_result result) {
                    copy_lease->release_root();
                    const auto state = weak_state.lock();
                    if (!state) return;
                    if (result.status != imaging::device_submission_status::completed) {
                        state->notify_lifecycle(result.status == imaging::device_submission_status::device_lost
                                ? capture_lifecycle::device_lost
                                : capture_lifecycle::unsupported);
                        return;
                    }
                    if (!state->is_running.load() ||
                        state->latest_requested_sequence.load() != identity.frame_sequence) return;
                    auto output_lease = std::make_shared<frame_lease>(identity, [] {});
                    auto output = std::make_shared<captured_frame>(captured_frame{
                        identity,
                        static_cast<std::uint32_t>(size.Width),
                        static_cast<std::uint32_t>(size.Height),
                        dpi,
                        timestamp,
                        state->luid,
                        texture,
                        output_lease,
                    });
                    std::shared_ptr<frame_lease> replaced;
                    {
                        std::scoped_lock lock(state->gate);
                        replaced = std::exchange(state->latest_lease, output_lease);
                    }
                    if (replaced) replaced->release_root();
                    if (dpi != std::exchange(state->last_dpi, dpi))
                        state->notify_lifecycle(capture_lifecycle::dpi_changed);
                    state->emit_frame(std::move(output));
                });
            copy_lease->release_root();
            if (!queued) notify_lifecycle(capture_lifecycle::unsupported);
        } catch (const winrt::hresult_error& error) {
            notify_lifecycle(error.code() == DXGI_ERROR_DEVICE_REMOVED ||
                error.code() == DXGI_ERROR_DEVICE_RESET
                ? capture_lifecycle::device_lost
                : capture_lifecycle::unsupported);
        } catch (...) {
            notify_lifecycle(capture_lifecycle::unsupported);
        }
    }
};

windows_capture_source::windows_capture_source(
    capture_source_descriptor descriptor,
    ID3D11Device* device,
    imaging::device_runtime& device_runtime,
    const std::uint64_t device_epoch)
    : impl_(std::make_shared<implementation>()) {
    const bool invalid_window = descriptor.kind == capture_source_kind::window && descriptor.window == nullptr;
    const bool invalid_monitor = descriptor.kind == capture_source_kind::monitor && descriptor.monitor == nullptr;
    if (descriptor.key == 0U || device == nullptr || device_epoch == 0U || invalid_window || invalid_monitor)
        throw std::invalid_argument("invalid capture source");
    impl_->descriptor = descriptor;
    impl_->d3d_device = device;
    impl_->device_runtime = &device_runtime;
    impl_->device_epoch = device_epoch;
    impl_->luid = adapter_luid(device);
}

windows_capture_source::~windows_capture_source() { stop(); }

void windows_capture_source::start(frame_callback on_frame, lifecycle_callback on_lifecycle) {
    if (!on_frame || impl_->is_running.exchange(true))
        throw std::logic_error("capture source is already running or has no callback");
    try {
        {
            std::scoped_lock lock(impl_->gate);
            impl_->on_frame = std::move(on_frame);
            impl_->on_lifecycle = std::move(on_lifecycle);
        }
        impl_->direct3d_device = create_direct3d_device(impl_->d3d_device.Get());
        impl_->item = create_item(impl_->descriptor);
        impl_->pool_size = impl_->item.Size();
        impl_->frame_pool = Direct3D11CaptureFramePool::CreateFreeThreaded(
            impl_->direct3d_device,
            DirectXPixelFormat::B8G8R8A8UIntNormalized,
            3,
            impl_->pool_size);
        impl_->session = impl_->frame_pool.CreateCaptureSession(impl_->item);
        impl_->session.IsCursorCaptureEnabled(false);
        bool border_required{};
        try {
            impl_->session.IsBorderRequired(false);
        } catch (const winrt::hresult_access_denied&) {
            border_required = true;
        }
        impl_->frame_arrived_token = impl_->frame_pool.FrameArrived(
            [weak_state = std::weak_ptr<implementation>{impl_}](const auto& sender, const auto&) {
                if (const auto state = weak_state.lock()) state->frame_arrived(sender);
            });
        impl_->session.StartCapture();
        impl_->notify_lifecycle(border_required
            ? capture_lifecycle::border_required
            : capture_lifecycle::running);
    } catch (...) {
        stop();
        throw;
    }
}

void windows_capture_source::stop() noexcept {
    if (!impl_) return;
    impl_->is_running.store(false);
    if (!impl_->session && !impl_->frame_pool && !impl_->item) return;
    try {
        if (impl_->frame_pool && impl_->frame_arrived_token.value != 0)
            impl_->frame_pool.FrameArrived(impl_->frame_arrived_token);
        if (impl_->session) impl_->session.Close();
        if (impl_->frame_pool) impl_->frame_pool.Close();
    } catch (...) { }
    std::shared_ptr<frame_lease> lease;
    {
        std::scoped_lock lock(impl_->gate);
        lease = std::exchange(impl_->latest_lease, {});
        impl_->on_frame = {};
        impl_->on_lifecycle = {};
    }
    if (lease) lease->release_root();
    impl_->session = nullptr;
    impl_->frame_pool = nullptr;
    impl_->item = nullptr;
    impl_->direct3d_device = nullptr;
    impl_->frame_arrived_token = {};
}

capture_source_descriptor windows_capture_source::descriptor() const noexcept { return impl_->descriptor; }
bool windows_capture_source::running() const noexcept { return impl_->is_running.load(); }

} // namespace infini::capture

#endif
