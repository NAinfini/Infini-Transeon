#include "infini/overlay/overlay_compositor.hpp"

#ifdef _WIN32

#include "infini/overlay/background_treatment.hpp"
#include "infini/overlay/text_layout.hpp"

#include <d2d1_1.h>
#include <dcomp.h>
#include <dwrite.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <algorithm>
#include <cmath>
#include <string>

namespace infini::overlay {
namespace {

template <typename T>
using com_ptr = Microsoft::WRL::ComPtr<T>;

[[nodiscard]] D2D1_COLOR_F to_d2d(const color_rgba value) noexcept {
    return {value.red, value.green, value.blue, value.alpha};
}

[[nodiscard]] std::wstring display_text(const slot& value) {
    std::wstring result(value.label.begin(), value.label.end());
    if (!result.empty()) result.append(L"  ");
    if (!value.text.empty()) result.append(value.text.begin(), value.text.end());
    else {
        switch (value.state) {
        case slot_state::waiting: result.append(L"\u2026"); break;
        case slot_state::timeout: result.append(L"\u23f1"); break;
        case slot_state::failure: result.append(L"\u00d7"); break;
        case slot_state::cancelled: result.append(L"\u2014"); break;
        default: break;
        }
    }
    return result;
}

} // namespace

struct overlay_compositor::implementation final {
    HWND window{};
    std::uint32_t width{};
    std::uint32_t height{};
    com_ptr<ID3D11Device> d3d_device;
    com_ptr<IDXGISwapChain1> swap_chain;
    com_ptr<IDCompositionDevice> composition_device;
    com_ptr<IDCompositionTarget> composition_target;
    com_ptr<IDCompositionVisual> composition_visual;
    com_ptr<ID2D1Factory1> d2d_factory;
    com_ptr<ID2D1Device> d2d_device;
    com_ptr<ID2D1DeviceContext> d2d_context;
    com_ptr<ID2D1Bitmap1> target_bitmap;
    com_ptr<IDWriteFactory> write_factory;

    [[nodiscard]] HRESULT create_target_bitmap() noexcept {
        target_bitmap.Reset();
        com_ptr<IDXGISurface> surface;
        HRESULT result = swap_chain->GetBuffer(0U, IID_PPV_ARGS(&surface));
        if (FAILED(result)) return result;
        const D2D1_BITMAP_PROPERTIES1 properties = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
        result = d2d_context->CreateBitmapFromDxgiSurface(surface.Get(), &properties, &target_bitmap);
        if (SUCCEEDED(result)) d2d_context->SetTarget(target_bitmap.Get());
        return result;
    }
};

overlay_compositor::overlay_compositor() noexcept
    : owner_thread_(GetCurrentThreadId()), impl_(std::make_unique<implementation>()) {}

overlay_compositor::~overlay_compositor() { reset(); }

compositor_error overlay_compositor::initialize(
    const HWND overlay_window,
    ID3D11Device* const device,
    const std::uint32_t width,
    const std::uint32_t height) noexcept {
    if (!on_owner_thread()) return compositor_error::wrong_thread;
    if (overlay_window == nullptr || device == nullptr || width == 0U || height == 0U)
        return compositor_error::invalid_argument;
    reset();
    impl_->window = overlay_window;
    impl_->width = width;
    impl_->height = height;
    impl_->d3d_device = device;

    com_ptr<IDXGIDevice> dxgi_device;
    if (FAILED(device->QueryInterface(IID_PPV_ARGS(&dxgi_device))))
        return compositor_error::device_creation_failed;
    com_ptr<IDXGIAdapter> adapter;
    if (FAILED(dxgi_device->GetAdapter(&adapter))) return compositor_error::device_creation_failed;
    com_ptr<IDXGIFactory2> factory;
    if (FAILED(adapter->GetParent(IID_PPV_ARGS(&factory))))
        return compositor_error::device_creation_failed;

    DXGI_SWAP_CHAIN_DESC1 description{};
    description.Width = width;
    description.Height = height;
    description.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    description.SampleDesc.Count = 1U;
    description.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
    description.BufferCount = 2U;
    description.Scaling = DXGI_SCALING_STRETCH;
    description.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
    description.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
    if (FAILED(factory->CreateSwapChainForComposition(
            device, &description, nullptr, &impl_->swap_chain)))
        return compositor_error::composition_creation_failed;

    if (FAILED(DCompositionCreateDevice(dxgi_device.Get(), IID_PPV_ARGS(&impl_->composition_device))) ||
        FAILED(impl_->composition_device->CreateTargetForHwnd(
            overlay_window, TRUE, &impl_->composition_target)) ||
        FAILED(impl_->composition_device->CreateVisual(&impl_->composition_visual)) ||
        FAILED(impl_->composition_visual->SetContent(impl_->swap_chain.Get())) ||
        FAILED(impl_->composition_target->SetRoot(impl_->composition_visual.Get())))
        return compositor_error::composition_creation_failed;

    D2D1_FACTORY_OPTIONS factory_options{};
    if (FAILED(D2D1CreateFactory(
            D2D1_FACTORY_TYPE_MULTI_THREADED,
            __uuidof(ID2D1Factory1),
            &factory_options,
            reinterpret_cast<void**>(impl_->d2d_factory.GetAddressOf()))) ||
        FAILED(impl_->d2d_factory->CreateDevice(dxgi_device.Get(), &impl_->d2d_device)) ||
        FAILED(impl_->d2d_device->CreateDeviceContext(
            D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &impl_->d2d_context)) ||
        FAILED(DWriteCreateFactory(
            DWRITE_FACTORY_TYPE_SHARED,
            __uuidof(IDWriteFactory),
            reinterpret_cast<IUnknown**>(impl_->write_factory.GetAddressOf()))) ||
        FAILED(impl_->create_target_bitmap()))
        return compositor_error::device_creation_failed;
    return FAILED(impl_->composition_device->Commit())
        ? compositor_error::commit_failed
        : compositor_error::none;
}

compositor_error overlay_compositor::resize(
    const std::uint32_t width,
    const std::uint32_t height) noexcept {
    if (!on_owner_thread()) return compositor_error::wrong_thread;
    if (!impl_->swap_chain || width == 0U || height == 0U) return compositor_error::invalid_argument;
    if (width == impl_->width && height == impl_->height) return compositor_error::none;
    impl_->d2d_context->SetTarget(nullptr);
    impl_->target_bitmap.Reset();
    if (FAILED(impl_->swap_chain->ResizeBuffers(
            2U, width, height, DXGI_FORMAT_B8G8R8A8_UNORM, 0U)) ||
        FAILED(impl_->create_target_bitmap()))
        return compositor_error::device_creation_failed;
    impl_->width = width;
    impl_->height = height;
    return compositor_error::none;
}

compositor_error overlay_compositor::render(
    const desired_state& state,
    const float dpi_scale,
    const desired_state* const previous,
    const float refinement_progress,
    const std::uint32_t transition_milliseconds) noexcept {
    if (!on_owner_thread()) return compositor_error::wrong_thread;
    if (!impl_->d2d_context || dpi_scale <= 0.0F ||
        !std::isfinite(refinement_progress) || refinement_progress < 0.0F ||
        refinement_progress > 1.0F) return compositor_error::invalid_argument;
    impl_->d2d_context->BeginDraw();
    impl_->d2d_context->Clear(D2D1_COLOR_F{0.0F, 0.0F, 0.0F, 0.0F});
    for (const region& region : state.regions) {
        const color_rgba sampled_background = region.background_pixels
            ? region.background_pixels->average
            : region.style.background_color;
        background_result treatment = resolve_background(
            region.style,
            sampled_background,
            region.style.background == background_mode::temporal_cache &&
                region.background_pixels != nullptr);
        const rect_f display = display_bounds(region);
        const D2D1_RECT_F region_bounds = D2D1::RectF(
            display.x,
            display.y,
            display.x + display.width,
            display.y + display.height);
        if (region.background_pixels) {
            const background_frame& pixels = *region.background_pixels;
            const D2D1_BITMAP_PROPERTIES properties = D2D1::BitmapProperties(
                D2D1::PixelFormat(
                    DXGI_FORMAT_B8G8R8A8_UNORM,
                    D2D1_ALPHA_MODE_PREMULTIPLIED));
            com_ptr<ID2D1Bitmap> background_bitmap;
            if (FAILED(impl_->d2d_context->CreateBitmap(
                    D2D1::SizeU(pixels.width, pixels.height),
                    pixels.pixels.data(),
                    pixels.stride,
                    properties,
                    &background_bitmap)))
                return compositor_error::drawing_failed;
            const float opacity = treatment.uses_cached_pixels
                ? 1.0F
                : (std::clamp)(treatment.background.alpha, 0.0F, 1.0F);
            impl_->d2d_context->DrawBitmap(
                background_bitmap.Get(),
                region_bounds,
                opacity,
                D2D1_BITMAP_INTERPOLATION_MODE_LINEAR);
        } else if (region.style.background != background_mode::no_cover) {
            com_ptr<ID2D1SolidColorBrush> background_brush;
            if (FAILED(impl_->d2d_context->CreateSolidColorBrush(
                    to_d2d(treatment.background), &background_brush)))
                return compositor_error::drawing_failed;
            impl_->d2d_context->FillRectangle(region_bounds, background_brush.Get());
        }
        const auto* previous_region =
            static_cast<const infini::overlay::region*>(nullptr);
        if (previous != nullptr) {
            const auto iterator = std::ranges::find_if(
                previous->regions,
                [&region](const infini::overlay::region& value) {
                    return value.id == region.id;
                });
            if (iterator != previous->regions.end()) previous_region = &*iterator;
        }
        for (const slot_layout& layout : layout_fixed_slots(region, dpi_scale)) {
            const auto slot_iterator = std::find_if(
                region.ordered_slots.begin(), region.ordered_slots.end(),
                [&layout](const slot& item) { return item.id == layout.slot_id; });
            if (slot_iterator == region.ordered_slots.end()) continue;
            const float padding = region.style.padding * dpi_scale;
            const D2D1_RECT_F bounds = D2D1::RectF(
                layout.bounds.x + padding,
                layout.bounds.y,
                layout.bounds.x + layout.bounds.width - padding,
                layout.bounds.y + layout.bounds.height);
            const auto draw_slot = [&](const slot& value, const float opacity) -> bool {
                if (opacity <= 0.0F) return true;
                std::wstring text = display_text(value);
                com_ptr<IDWriteTextFormat> format;
                if (FAILED(impl_->write_factory->CreateTextFormat(
                        L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL,
                        DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL,
                        layout.font_size, L"", &format))) return false;
                const DWRITE_TEXT_ALIGNMENT alignment =
                    region.style.alignment == text_alignment::center
                        ? DWRITE_TEXT_ALIGNMENT_CENTER
                        : region.style.alignment == text_alignment::right
                            ? DWRITE_TEXT_ALIGNMENT_TRAILING
                            : DWRITE_TEXT_ALIGNMENT_LEADING;
                static_cast<void>(format->SetTextAlignment(alignment));
                static_cast<void>(format->SetParagraphAlignment(DWRITE_PARAGRAPH_ALIGNMENT_CENTER));
                static_cast<void>(format->SetWordWrapping(DWRITE_WORD_WRAPPING_WRAP));
                com_ptr<IDWriteInlineObject> trimming_sign;
                if (layout.overflow) {
                    const DWRITE_TRIMMING trimming{
                        DWRITE_TRIMMING_GRANULARITY_CHARACTER, 0U, 0U};
                    if (SUCCEEDED(impl_->write_factory->CreateEllipsisTrimmingSign(
                            format.Get(), &trimming_sign)))
                        static_cast<void>(format->SetTrimming(&trimming, trimming_sign.Get()));
                }
                color_rgba text_color = treatment.text;
                text_color.alpha *= opacity;
                com_ptr<ID2D1SolidColorBrush> text_brush;
                if (FAILED(impl_->d2d_context->CreateSolidColorBrush(
                        to_d2d(text_color), &text_brush))) return false;
                com_ptr<ID2D1SolidColorBrush> outline_brush;
                if (region.style.outline_width > 0.0F) {
                    color_rgba outline_color = region.style.outline_color;
                    outline_color.alpha *= opacity;
                    if (FAILED(impl_->d2d_context->CreateSolidColorBrush(
                            to_d2d(outline_color), &outline_brush))) return false;
                }
                if (outline_brush) {
                    const float outline = region.style.outline_width * dpi_scale;
                    for (const D2D1_POINT_2F offset : {
                            D2D1::Point2F(-outline, 0.0F),
                            D2D1::Point2F(outline, 0.0F),
                            D2D1::Point2F(0.0F, -outline),
                            D2D1::Point2F(0.0F, outline)}) {
                        const D2D1_RECT_F outline_bounds = D2D1::RectF(
                            bounds.left + offset.x, bounds.top + offset.y,
                            bounds.right + offset.x, bounds.bottom + offset.y);
                        impl_->d2d_context->DrawText(
                            text.c_str(), static_cast<UINT32>(text.size()), format.Get(),
                            outline_bounds, outline_brush.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP,
                            DWRITE_MEASURING_MODE_NATURAL);
                    }
                }
                impl_->d2d_context->DrawText(
                    text.c_str(), static_cast<UINT32>(text.size()), format.Get(), bounds,
                    text_brush.Get(), D2D1_DRAW_TEXT_OPTIONS_CLIP,
                    DWRITE_MEASURING_MODE_NATURAL);
                return true;
            };
            const slot* previous_slot = nullptr;
            if (previous_region != nullptr) {
                const auto iterator = std::ranges::find_if(
                    previous_region->ordered_slots,
                    [&slot_iterator](const slot& value) { return value.id == slot_iterator->id; });
                if (iterator != previous_region->ordered_slots.end() &&
                    slot_iterator->stage_index > iterator->stage_index &&
                    slot_iterator->text != iterator->text)
                    previous_slot = &*iterator;
            }
            const float region_progress = region.style.reduced_motion ||
                    region.style.crossfade_milliseconds == 0U
                ? 1.0F
                : transition_milliseconds == 0U
                    ? refinement_progress
                    : (std::min)(1.0F,
                        refinement_progress *
                            static_cast<float>(transition_milliseconds) /
                            static_cast<float>(region.style.crossfade_milliseconds));
            if (previous_slot != nullptr &&
                !draw_slot(*previous_slot, 1.0F - region_progress))
                return compositor_error::drawing_failed;
            if (!draw_slot(*slot_iterator,
                    previous_slot == nullptr ? 1.0F : region_progress))
                return compositor_error::drawing_failed;
        }
    }
    HRESULT result = impl_->d2d_context->EndDraw();
    if (FAILED(result)) return compositor_error::drawing_failed;
    result = impl_->swap_chain->Present(1U, 0U);
    if (FAILED(result)) return compositor_error::drawing_failed;
    return FAILED(impl_->composition_device->Commit())
        ? compositor_error::commit_failed
        : compositor_error::none;
}

void overlay_compositor::reset() noexcept {
    if (!impl_ || !on_owner_thread()) return;
    if (impl_->d2d_context) impl_->d2d_context->SetTarget(nullptr);
    impl_ = std::make_unique<implementation>();
}

bool overlay_compositor::on_owner_thread() const noexcept {
    return owner_thread_ == GetCurrentThreadId();
}

} // namespace infini::overlay

#endif
