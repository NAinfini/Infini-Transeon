add_library(InfiniTranseon.EngineHost.Runtime STATIC
    ipc/runtime_capture_controller.cpp
    ipc/runtime_protocol.cpp
    ipc/runtime_server.cpp
)

target_include_directories(InfiniTranseon.EngineHost.Runtime PUBLIC
    "${CMAKE_CURRENT_SOURCE_DIR}/ipc"
)
target_compile_features(InfiniTranseon.EngineHost.Runtime PUBLIC cxx_std_20)
target_link_libraries(InfiniTranseon.EngineHost.Runtime PUBLIC
    InfiniTranseon.Engine.Native
)
target_link_libraries(InfiniTranseon.EngineHost.Runtime PRIVATE
    advapi32
    psapi
)

add_executable(InfiniTranseon.EngineHost main.cpp)

target_compile_features(InfiniTranseon.EngineHost PRIVATE cxx_std_20)
target_link_libraries(InfiniTranseon.EngineHost PRIVATE
    InfiniTranseon.Engine.Native
    InfiniTranseon.EngineHost.Runtime
)

if(MSVC)
    target_compile_options(InfiniTranseon.EngineHost.Runtime PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
    target_compile_options(InfiniTranseon.EngineHost PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

infini_copy_asan_runtime(InfiniTranseon.EngineHost)

if(BUILD_TESTING)
    add_test(
        NAME infini_engine_host_abi_version
        COMMAND InfiniTranseon.EngineHost --abi-version
    )
    set_tests_properties(infini_engine_host_abi_version PROPERTIES
        PASS_REGULAR_EXPRESSION "^1"
    )
    infini_configure_test_runtime(infini_engine_host_abi_version)

    add_test(NAME infini_engine_host_rejects_missing_bootstrap COMMAND
        powershell -NoProfile -ExecutionPolicy Bypass
        -File "${PROJECT_SOURCE_DIR}/tests/InfiniTranseon.Engine.Tests/engine_host_cli_tests.ps1"
        $<TARGET_FILE:InfiniTranseon.EngineHost>
    )
    infini_configure_test_runtime(infini_engine_host_rejects_missing_bootstrap)
endif()
