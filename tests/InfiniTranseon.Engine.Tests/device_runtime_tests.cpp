#include "infini/capture/frame_lease.hpp"
#include "infini/imaging/device_runtime.hpp"
#include "infini/ocr/texture_crop_readback.hpp"

#include <d3d11.h>
#include <wrl/client.h>

#include <atomic>
#include <array>
#include <cassert>
#include <chrono>
#include <condition_variable>
#include <mutex>
#include <memory>

int main() {
    using namespace infini::capture;
    using namespace infini::imaging;
    Microsoft::WRL::ComPtr<ID3D11Device> device;
    D3D_FEATURE_LEVEL level{};
    assert(SUCCEEDED(D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_WARP,
        nullptr,
        0U,
        nullptr,
        0U,
        D3D11_SDK_VERSION,
        &device,
        &level,
        nullptr)));

    std::mutex gate;
    std::condition_variable changed;
    bool completed = false;
    device_submission_result result{};
    std::atomic<int> closes{};
    frame_lease lease({1U, 1U, 1U, 1U}, [&closes] { ++closes; });
    device_runtime runtime(device.Get(), 1U, 2U, std::chrono::milliseconds{1000});
    assert(runtime.enqueue(
        lease.acquire_ticket(),
        [](ID3D11DeviceContext* context) {
            context->Flush();
            return S_OK;
        },
        [&](const device_submission_result value) {
            {
                std::scoped_lock lock(gate);
                result = value;
                completed = true;
            }
            changed.notify_one();
        }));
    lease.release_root();
    {
        std::unique_lock lock(gate);
        assert(changed.wait_for(lock, std::chrono::seconds{3}, [&] { return completed; }));
    }
    assert(result.status == device_submission_status::completed);
    assert(result.device_epoch == 1U);
    assert(closes.load() == 1);

    constexpr std::array<std::uint8_t, 16U> pixels{
        1U, 2U, 3U, 255U, 10U, 20U, 30U, 255U,
        4U, 5U, 6U, 255U, 40U, 50U, 60U, 255U,
    };
    D3D11_TEXTURE2D_DESC texture_description{};
    texture_description.Width = 2U;
    texture_description.Height = 2U;
    texture_description.MipLevels = 1U;
    texture_description.ArraySize = 1U;
    texture_description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    texture_description.SampleDesc.Count = 1U;
    texture_description.Usage = D3D11_USAGE_DEFAULT;
    texture_description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA initial{pixels.data(), 8U, 16U};
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    assert(SUCCEEDED(device->CreateTexture2D(
        &texture_description, &initial, &texture)));
    auto crop_lease = std::make_shared<frame_lease>(
        frame_identity{2U, 2U, 1U, 2U}, [] {});
    auto frame = std::make_shared<captured_frame>(captured_frame{
        crop_lease->identity(), 2U, 2U, 96U, 1, {}, texture, crop_lease,
    });
    const infini::ocr::texture_crop_readback_result crop =
        infini::ocr::readback_texture_crop(
            device.Get(), runtime, frame, {1U, 0U, 1U, 2U}, 1024U,
            std::chrono::seconds(2));
    crop_lease->release_root();
    assert(crop.status == infini::ocr::texture_crop_readback_status::succeeded);
    assert(crop.image.width == 1U && crop.image.height == 2U);
    assert(std::to_integer<std::uint8_t>(crop.image.pixels[0]) == 10U);
    assert(std::to_integer<std::uint8_t>(crop.image.pixels[4]) == 40U);
    runtime.stop();
    assert(!runtime.enqueue({}, [](ID3D11DeviceContext*) { return S_OK; }, {}));
    return 0;
}
