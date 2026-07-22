add_library(InfiniTranseon.Engine.Native STATIC
    src/abi/infini_engine.cpp
    src/capture/frame_lease.cpp
    src/capture/capture_targets.cpp
    src/capture/capture_session_manager.cpp
    src/capture/windows_capture_source.cpp
    src/capture/window_target_inspector.cpp
    src/diagnostics/performance_sampler.cpp
    src/imaging/change_detector.cpp
    src/imaging/coordinate_mapper.cpp
    src/imaging/device_runtime.cpp
    src/imaging/text_box_tracker.cpp
    src/ocr/ocr_engine.cpp
    src/ocr/cloud_crop_encoder.cpp
    src/ocr/onnx_session_pool.cpp
    src/ocr/preprocessing_pipeline.cpp
    src/ocr/readback_ring.cpp
    src/ocr/texture_crop_readback.cpp
    src/ocr/windows_media_ocr.cpp
    src/overlay/background_treatment.cpp
    src/overlay/overlay_compositor.cpp
    src/overlay/overlay_renderer.cpp
    src/overlay/overlay_runtime.cpp
    src/overlay/text_layout.cpp
    src/overlay/overlay_window.cpp
    src/scheduling/generation_registry.cpp
    src/scheduling/region_scheduler.cpp
)

target_include_directories(InfiniTranseon.Engine.Native PUBLIC
    "$<BUILD_INTERFACE:${CMAKE_CURRENT_SOURCE_DIR}/include>"
)

target_compile_features(InfiniTranseon.Engine.Native PUBLIC cxx_std_20)
target_link_libraries(InfiniTranseon.Engine.Native PUBLIC
    windowsapp user32 d3d11 dxgi d2d1 dwrite dcomp dwmapi ole32 windowscodecs)

if(MSVC)
    target_compile_options(InfiniTranseon.Engine.Native PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()
