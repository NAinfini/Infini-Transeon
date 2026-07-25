add_executable(InfiniTranseon.CaptureSpike
    main.cpp
)

target_compile_features(InfiniTranseon.CaptureSpike PRIVATE cxx_std_20)
target_link_libraries(InfiniTranseon.CaptureSpike PRIVATE windowsapp user32 gdi32 dxgi)

if(MSVC)
    target_link_options(InfiniTranseon.CaptureSpike PRIVATE
        "/MANIFESTINPUT:${PROJECT_SOURCE_DIR}/src/InfiniTranseon.Engine.Native/resources/infini_native.manifest"
    )
    target_compile_options(InfiniTranseon.CaptureSpike PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

infini_copy_asan_runtime(InfiniTranseon.CaptureSpike)

if(BUILD_TESTING)
    add_test(
        NAME infini_capture_spike_help
        COMMAND InfiniTranseon.CaptureSpike --help
    )
    infini_configure_test_runtime(infini_capture_spike_help)
endif()
