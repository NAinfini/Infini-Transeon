if(MSVC)
    string(REPLACE "/DNDEBUG" "" CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE}")
    string(REPLACE "/DNDEBUG" "" CMAKE_CXX_FLAGS_RELWITHDEBINFO "${CMAKE_CXX_FLAGS_RELWITHDEBINFO}")
endif()

add_executable(infini_engine_abi_contract_tests
    abi_contract_tests.cpp
)

target_include_directories(infini_engine_abi_contract_tests PRIVATE
    "${PROJECT_SOURCE_DIR}/src/InfiniTranseon.Engine.Native/include"
)

target_compile_features(infini_engine_abi_contract_tests PRIVATE cxx_std_20)
target_link_libraries(infini_engine_abi_contract_tests PRIVATE InfiniTranseon.Engine.Native)

if(MSVC)
    target_compile_options(infini_engine_abi_contract_tests PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

add_test(NAME infini_engine_abi_contract_tests COMMAND infini_engine_abi_contract_tests)
infini_configure_test_runtime(infini_engine_abi_contract_tests)

add_executable(infini_engine_runtime_protocol_tests
    runtime_protocol_tests.cpp
)

target_compile_features(infini_engine_runtime_protocol_tests PRIVATE cxx_std_20)
target_link_libraries(infini_engine_runtime_protocol_tests PRIVATE InfiniTranseon.EngineHost.Runtime)

if(MSVC)
    target_compile_options(infini_engine_runtime_protocol_tests PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

add_test(NAME infini_engine_runtime_protocol_tests COMMAND infini_engine_runtime_protocol_tests)
infini_configure_test_runtime(infini_engine_runtime_protocol_tests)

add_executable(infini_coordinate_mapper_tests
    coordinate_mapper_tests.cpp
)

target_compile_features(infini_coordinate_mapper_tests PRIVATE cxx_std_20)
target_link_libraries(infini_coordinate_mapper_tests PRIVATE InfiniTranseon.Engine.Native)

if(MSVC)
    target_compile_options(infini_coordinate_mapper_tests PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

add_test(NAME infini_coordinate_mapper_tests COMMAND infini_coordinate_mapper_tests)
infini_configure_test_runtime(infini_coordinate_mapper_tests)

add_executable(infini_scheduler_tests
    scheduler_tests.cpp
)

target_compile_features(infini_scheduler_tests PRIVATE cxx_std_20)
target_link_libraries(infini_scheduler_tests PRIVATE InfiniTranseon.Engine.Native)

if(MSVC)
    target_compile_options(infini_scheduler_tests PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

add_test(NAME infini_scheduler_tests COMMAND infini_scheduler_tests)
infini_configure_test_runtime(infini_scheduler_tests)

add_executable(infini_ocr_pipeline_tests
    ocr_pipeline_tests.cpp
)

target_compile_features(infini_ocr_pipeline_tests PRIVATE cxx_std_20)
target_link_libraries(infini_ocr_pipeline_tests PRIVATE InfiniTranseon.Engine.Native)

if(MSVC)
    target_compile_options(infini_ocr_pipeline_tests PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

add_test(NAME infini_ocr_pipeline_tests COMMAND infini_ocr_pipeline_tests)
infini_configure_test_runtime(infini_ocr_pipeline_tests)

add_executable(infini_overlay_layout_tests
    overlay_layout_tests.cpp
)

target_compile_features(infini_overlay_layout_tests PRIVATE cxx_std_20)
target_link_libraries(infini_overlay_layout_tests PRIVATE InfiniTranseon.Engine.Native)

if(MSVC)
    target_compile_options(infini_overlay_layout_tests PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

add_test(NAME infini_overlay_layout_tests COMMAND infini_overlay_layout_tests)
infini_configure_test_runtime(infini_overlay_layout_tests)

add_executable(infini_frame_lease_tests frame_lease_tests.cpp)
target_compile_features(infini_frame_lease_tests PRIVATE cxx_std_20)
target_link_libraries(infini_frame_lease_tests PRIVATE InfiniTranseon.Engine.Native)
if(MSVC)
    target_compile_options(infini_frame_lease_tests PRIVATE /W4 /WX /permissive- /sdl)
endif()
add_test(NAME infini_frame_lease_tests COMMAND infini_frame_lease_tests)
infini_configure_test_runtime(infini_frame_lease_tests)

add_executable(infini_capture_target_tests capture_target_tests.cpp)
target_compile_features(infini_capture_target_tests PRIVATE cxx_std_20)
target_link_libraries(infini_capture_target_tests PRIVATE InfiniTranseon.Engine.Native)
if(MSVC)
    target_compile_options(infini_capture_target_tests PRIVATE /W4 /WX /permissive- /sdl)
endif()
add_test(NAME infini_capture_target_tests COMMAND infini_capture_target_tests)
infini_configure_test_runtime(infini_capture_target_tests)

add_executable(infini_device_runtime_tests device_runtime_tests.cpp)
target_compile_features(infini_device_runtime_tests PRIVATE cxx_std_20)
target_link_libraries(infini_device_runtime_tests PRIVATE InfiniTranseon.Engine.Native)
if(MSVC)
    target_compile_options(infini_device_runtime_tests PRIVATE /W4 /WX /permissive- /sdl)
endif()
add_test(NAME infini_device_runtime_tests COMMAND infini_device_runtime_tests)
infini_configure_test_runtime(infini_device_runtime_tests)

add_executable(infini_window_target_inspector_tests window_target_inspector_tests.cpp)
target_compile_features(infini_window_target_inspector_tests PRIVATE cxx_std_20)
target_link_libraries(infini_window_target_inspector_tests PRIVATE InfiniTranseon.Engine.Native)
if(MSVC)
    target_compile_options(infini_window_target_inspector_tests PRIVATE /W4 /WX /permissive- /sdl)
endif()
add_test(NAME infini_window_target_inspector_tests COMMAND infini_window_target_inspector_tests)
infini_configure_test_runtime(infini_window_target_inspector_tests)
