cmake_minimum_required(VERSION 3.28)

project(InfiniTranseon LANGUAGES CXX)

include(CTest)

if(MSVC)
    # Release artifacts must run on a clean supported Windows 11 installation without a
    # separately installed Visual C++ Redistributable. Keep the CRT inside every native
    # executable/library; sanitizer builds still copy their distinct ASan runtime below.
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    add_compile_options(/EHsc)
endif()

option(INFINI_ENABLE_ADDRESS_SANITIZER "Enable AddressSanitizer for native test builds" OFF)
option(INFINI_BUILD_SPIKES "Build development-only platform and capture spikes" OFF)
option(
    INFINI_ENABLE_LOCAL_MODEL_RUNTIME
    "Build the optional CTranslate2/SentencePiece local translation runtime"
    OFF
)
if(INFINI_ENABLE_ADDRESS_SANITIZER)
    if(NOT MSVC)
        message(FATAL_ERROR "The supported native sanitizer preset currently requires MSVC.")
    endif()
    add_compile_options(/fsanitize=address /Zi)
    add_link_options(/INCREMENTAL:NO)

    if(DEFINED ENV{VCToolsInstallDir} AND
       EXISTS "$ENV{VCToolsInstallDir}/bin/Hostx64/x64")
        file(TO_CMAKE_PATH "$ENV{VCToolsInstallDir}/bin/Hostx64/x64"
            INFINI_ASAN_RUNTIME_DIR)
    else()
        file(READ
            "${CMAKE_GENERATOR_INSTANCE}/VC/Auxiliary/Build/Microsoft.VCToolsVersion.default.txt"
            INFINI_MSVC_TOOLS_VERSION)
        string(STRIP "${INFINI_MSVC_TOOLS_VERSION}" INFINI_MSVC_TOOLS_VERSION)
        set(INFINI_ASAN_RUNTIME_DIR
            "${CMAKE_GENERATOR_INSTANCE}/VC/Tools/MSVC/${INFINI_MSVC_TOOLS_VERSION}/bin/Hostx64/x64")
    endif()
    if(NOT EXISTS "${INFINI_ASAN_RUNTIME_DIR}/clang_rt.asan_dynamic-x86_64.dll")
        message(FATAL_ERROR "MSVC AddressSanitizer runtime was not found in ${INFINI_ASAN_RUNTIME_DIR}.")
    endif()
endif()

function(infini_copy_asan_runtime target_name)
    if(INFINI_ENABLE_ADDRESS_SANITIZER)
        add_custom_command(TARGET ${target_name} POST_BUILD
            COMMAND ${CMAKE_COMMAND} -E copy_if_different
                "${INFINI_ASAN_RUNTIME_DIR}/clang_rt.asan_dynamic-x86_64.dll"
                "$<TARGET_FILE_DIR:${target_name}>"
            VERBATIM
        )
    endif()
endfunction()

function(infini_configure_test_runtime test_name)
    if(INFINI_ENABLE_ADDRESS_SANITIZER)
        set_tests_properties(${test_name} PROPERTIES
            ENVIRONMENT_MODIFICATION "PATH=path_list_prepend:${INFINI_ASAN_RUNTIME_DIR}")
    endif()
endfunction()

add_subdirectory(src/InfiniTranseon.Engine.Native)
add_subdirectory(src/InfiniTranseon.EngineHost)
if(INFINI_ENABLE_LOCAL_MODEL_RUNTIME)
    add_subdirectory(src/InfiniTranseon.ModelRuntime.Native)
endif()
if(INFINI_BUILD_SPIKES)
    add_subdirectory(spikes/InfiniTranseon.CaptureSpike)
endif()

if(BUILD_TESTING)
    add_subdirectory(tests/InfiniTranseon.Engine.Tests)
endif()
