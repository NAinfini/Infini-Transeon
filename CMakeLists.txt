cmake_minimum_required(VERSION 3.28)

project(InfiniTranseon LANGUAGES CXX)

include(CTest)

option(INFINI_ENABLE_ADDRESS_SANITIZER "Enable AddressSanitizer for native test builds" OFF)
if(INFINI_ENABLE_ADDRESS_SANITIZER)
    if(NOT MSVC)
        message(FATAL_ERROR "The supported native sanitizer preset currently requires MSVC.")
    endif()
    add_compile_options(/fsanitize=address /Zi)
    add_link_options(/INCREMENTAL:NO)

    file(READ
        "${CMAKE_GENERATOR_INSTANCE}/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt"
        INFINI_MSVC_TOOLS_VERSION)
    string(STRIP "${INFINI_MSVC_TOOLS_VERSION}" INFINI_MSVC_TOOLS_VERSION)
    set(INFINI_ASAN_RUNTIME_DIR
        "${CMAKE_GENERATOR_INSTANCE}/VC/Tools/MSVC/${INFINI_MSVC_TOOLS_VERSION}/bin/Hostx64/x64")
    if(NOT EXISTS "${INFINI_ASAN_RUNTIME_DIR}/clang_rt.asan_dynamic-x86_64.dll")
        message(FATAL_ERROR "MSVC AddressSanitizer runtime was not found in ${INFINI_ASAN_RUNTIME_DIR}.")
    endif()
endif()

function(infini_configure_test_runtime test_name)
    if(INFINI_ENABLE_ADDRESS_SANITIZER)
        set_tests_properties(${test_name} PROPERTIES
            ENVIRONMENT_MODIFICATION "PATH=path_list_prepend:${INFINI_ASAN_RUNTIME_DIR}")
    endif()
endfunction()

add_subdirectory(src/InfiniTranseon.Engine.Native)
add_subdirectory(src/InfiniTranseon.EngineHost)
add_subdirectory(spikes/InfiniTranseon.CaptureSpike)

if(BUILD_TESTING)
    add_subdirectory(tests/InfiniTranseon.Engine.Tests)
endif()
