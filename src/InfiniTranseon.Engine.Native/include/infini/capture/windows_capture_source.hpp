#pragma once

#ifdef _WIN32

#include "infini/capture/frame_lease.hpp"

#include <d3d11.h>
#include <dxgi.h>
#include <windows.h>
#include <wrl/client.h>

#include <functional>
#include <memory>

namespace infini::imaging { class device_runtime; }

namespace infini::capture {

enum class capture_source_kind : std::uint8_t { window, monitor };
enum class capture_lifecycle : std::uint8_t {
    available,
    running,
    minimized,
    resized,
    dpi_changed,
    closed,
    unsupported,
    device_lost,
    adapter_changed,
    border_required,
};

struct capture_source_descriptor final {
    std::uint64_t key{};
    capture_source_kind kind{};
    HWND window{};
    HMONITOR monitor{};
};

struct captured_frame final {
    frame_identity identity{};
    std::uint32_t width{};
    std::uint32_t height{};
    std::uint32_t dpi{};
    std::int64_t qpc_timestamp{};
    LUID adapter_luid{};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    std::shared_ptr<frame_lease> lease;
};

class capture_source {
public:
    using frame_callback = std::function<void(std::shared_ptr<captured_frame>)>;
    using lifecycle_callback = std::function<void(capture_lifecycle)>;

    virtual ~capture_source() = default;
    virtual void start(frame_callback on_frame, lifecycle_callback on_lifecycle) = 0;
    virtual void stop() noexcept = 0;
    [[nodiscard]] virtual capture_source_descriptor descriptor() const noexcept = 0;
    [[nodiscard]] virtual bool running() const noexcept = 0;
};

class windows_capture_source final : public capture_source {
public:

    windows_capture_source(
        capture_source_descriptor descriptor,
        ID3D11Device* device,
        imaging::device_runtime& device_runtime,
        std::uint64_t device_epoch);
    ~windows_capture_source() override;
    windows_capture_source(const windows_capture_source&) = delete;
    windows_capture_source& operator=(const windows_capture_source&) = delete;

    void start(frame_callback on_frame, lifecycle_callback on_lifecycle) override;
    void stop() noexcept override;
    [[nodiscard]] capture_source_descriptor descriptor() const noexcept override;
    [[nodiscard]] bool running() const noexcept override;

private:
    struct implementation;
    std::shared_ptr<implementation> impl_;
};

} // namespace infini::capture

#endif
