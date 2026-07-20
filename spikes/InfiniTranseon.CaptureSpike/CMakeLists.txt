add_executable(InfiniTranseon.CaptureSpike
    main.cpp
)

target_compile_features(InfiniTranseon.CaptureSpike PRIVATE cxx_std_20)
target_link_libraries(InfiniTranseon.CaptureSpike PRIVATE windowsapp)

if(MSVC)
    target_compile_options(InfiniTranseon.CaptureSpike PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

if(BUILD_TESTING)
    add_test(
        NAME infini_capture_spike_help
        COMMAND InfiniTranseon.CaptureSpike --help
    )
endif()
