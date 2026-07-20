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
