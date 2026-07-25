include(FetchContent)

# Dependencies are built from immutable upstream commits or fixed-hash release archives.
# They are implementation details of this DLL and are never accepted from a model package.
set(CMAKE_POLICY_VERSION_MINIMUM 3.5 CACHE STRING "" FORCE)
set(WITH_MKL OFF CACHE BOOL "" FORCE)
set(WITH_DNNL OFF CACHE BOOL "" FORCE)
set(WITH_OPENBLAS OFF CACHE BOOL "" FORCE)
set(WITH_RUY ON CACHE BOOL "" FORCE)
set(WITH_CUDA OFF CACHE BOOL "" FORCE)
set(WITH_CUDNN OFF CACHE BOOL "" FORCE)
set(WITH_HIP OFF CACHE BOOL "" FORCE)
set(WITH_TENSOR_PARALLEL OFF CACHE BOOL "" FORCE)
set(WITH_FLASH_ATTN OFF CACHE BOOL "" FORCE)
set(OPENMP_RUNTIME NONE CACHE STRING "" FORCE)
set(ENABLE_CPU_DISPATCH ON CACHE BOOL "" FORCE)
set(BUILD_CLI OFF CACHE BOOL "" FORCE)
set(BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)

FetchContent_Declare(
    ctranslate2
    GIT_REPOSITORY https://github.com/OpenNMT/CTranslate2.git
    GIT_TAG 0d8bcd362ac75ef860ef161d6f0efad0ae439ff0
    GIT_SUBMODULES
        third_party/cpu_features
        third_party/ruy
        third_party/spdlog
    GIT_SUBMODULES_RECURSE TRUE
    GIT_PROGRESS TRUE
    # The vendored fmt inside this pin's spdlog cannot compile in Debug on a current MSVC STL.
    # See the patch script for the exact symbol and how to drop the patch.
    PATCH_COMMAND ${CMAKE_COMMAND} -P
        "${CMAKE_SOURCE_DIR}/cmake/patches/ctranslate2-vendored-fmt-msvc.cmake"
    EXCLUDE_FROM_ALL
)
FetchContent_MakeAvailable(ctranslate2)

set(SPM_ENABLE_SHARED OFF CACHE BOOL "" FORCE)
set(SPM_BUILD_TEST OFF CACHE BOOL "" FORCE)
set(SPM_ENABLE_TCMALLOC OFF CACHE BOOL "" FORCE)
FetchContent_Declare(
    sentencepiece
    URL https://github.com/google/sentencepiece/archive/refs/tags/v0.2.1.tar.gz
    URL_HASH SHA256=C1A59E9259C9653AD0ADE653DADFF074CD31F0A6FF2A11316F67BEE4189A8F1B
    DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    EXCLUDE_FROM_ALL
)
FetchContent_MakeAvailable(sentencepiece)

add_library(InfiniTranseon.ModelRuntime.Native SHARED
    src/model_runtime.cpp
)

target_include_directories(InfiniTranseon.ModelRuntime.Native
    PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE "${sentencepiece_SOURCE_DIR}/src"
)
target_compile_features(InfiniTranseon.ModelRuntime.Native PRIVATE cxx_std_20)
target_link_libraries(InfiniTranseon.ModelRuntime.Native PRIVATE
    ctranslate2
    sentencepiece-static
)
set_target_properties(InfiniTranseon.ModelRuntime.Native PROPERTIES
    OUTPUT_NAME "InfiniTranseon.ModelRuntime.Native"
)

if(MSVC)
    target_compile_options(InfiniTranseon.ModelRuntime.Native PRIVATE
        /W4
        /WX
        /permissive-
        /sdl
    )
endif()

if(BUILD_TESTING)
    add_executable(infini_model_runtime_abi_tests
        tests/model_runtime_abi_tests.cpp
    )
    target_compile_features(infini_model_runtime_abi_tests PRIVATE cxx_std_20)
    target_link_libraries(infini_model_runtime_abi_tests PRIVATE
        InfiniTranseon.ModelRuntime.Native
    )
    if(MSVC)
        target_compile_options(infini_model_runtime_abi_tests PRIVATE
            /W4
            /WX
            /permissive-
            /sdl
        )
    endif()
    add_custom_command(TARGET infini_model_runtime_abi_tests POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:InfiniTranseon.ModelRuntime.Native>"
            "$<TARGET_FILE_DIR:infini_model_runtime_abi_tests>"
        VERBATIM
    )
    add_test(
        NAME infini_model_runtime_abi_tests
        COMMAND infini_model_runtime_abi_tests
    )
endif()
